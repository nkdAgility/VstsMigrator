using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using MigrationTools._EngineV1.Configuration;
using MigrationTools._EngineV1.DataContracts;
using MigrationTools.DataContracts;
using Newtonsoft.Json;
using Serilog;

namespace MigrationTools
{
    public static class TfsExtensions
    {
        //public static IServiceCollection TfsObjectModelWorkerServices(this IServiceCollection collection, EngineConfiguration config)
        //{
        //    if (collection == null) throw new ArgumentNullException(nameof(collection));
        //    if (config == null) throw new ArgumentNullException(nameof(config));

        //   // return collection.AddTransient<IWorkItemSink, AzureDevOpsWorkItemSink>();
        //}

        //public static void SaveWorkItem(this IProcessor context, WorkItem workItem)
        //{
        //    if (workItem == null) throw new ArgumentNullException(nameof(workItem));
        //    workItem.Fields["System.ChangedBy"].Value = "Migration";
        //    workItem.Save();
        //}

        public static Dictionary<string, object> AsDictionary(this FieldCollection col)
        {
            var dict = new Dictionary<string, object>();
            for (var ix = 0; ix < col.Count; ix++)
            {
                dict.Add(col[ix].ReferenceName, col[ix].Value);
            }
            return dict;
        }

        public static TfsTeamProjectConfig AsTeamProjectConfig(this IMigrationClientConfig context)
        {
            return (TfsTeamProjectConfig)context;
        }

        public static WorkItemData AsWorkItemData(this WorkItem context, Dictionary<string, object> fieldsOfRevision = null)
        {
            var internalWorkItem = new WorkItemData
            {
                internalObject = context
            };

            internalWorkItem.RefreshWorkItem(fieldsOfRevision);
            return internalWorkItem;
        }

        public static WorkItemData GetRevision(this WorkItemData context, int rev)
        {
            var originalWi = (WorkItem)context.internalObject;
            var wid = new WorkItemData
            {
                // internalObject = context.internalObject
                // TODO: Had to revert to calling revision load again untill WorkItemMigrationContext.PopulateWorkItem can be updated to pull from WorkItemData
                internalObject = originalWi.Store.GetWorkItem(originalWi.Id, rev)
            };

            wid.RefreshWorkItem(context.Revisions[rev].Fields);

            return wid;
        }

        public static void RefreshWorkItem(this WorkItemData context, Dictionary<string, object> fieldsOfRevision = null)
        {
            var workItem = (WorkItem)context.internalObject;
            //
            context.Id = workItem.Id.ToString();
            context.Title = fieldsOfRevision != null ? fieldsOfRevision["System.Title"].ToString() : workItem.Title;
            context.ProjectName = workItem.Project?.Name;
            context.Type = fieldsOfRevision != null ? fieldsOfRevision["System.WorkItemType"].ToString() : workItem.Type.Name;
            context.Rev = fieldsOfRevision != null ? (int)fieldsOfRevision["System.Rev"] : workItem.Rev;
            context.ChangedDate = fieldsOfRevision != null ? (DateTime)fieldsOfRevision["System.ChangedDate"] : workItem.ChangedDate;

            // If fieldsOfRevision is provided we use this collection as we want to create a revised WorkItemData object
            context.Fields = fieldsOfRevision != null ? fieldsOfRevision : workItem.Fields.AsDictionary();

            // We only need to fill the revisions object if we create a WorkItemData object for the whole WorkItem and
            // we sort it here by Number using a SortedDictionary
            context.Revisions = fieldsOfRevision == null ? new SortedDictionary<int, RevisionItem>((from Revision x in workItem.Revisions
                                                                                                    select new RevisionItem()
                                                                                                    {
                                                                                                        Index = x.Index,
                                                                                                        Number = (int)x.Fields["System.Rev"].Value,
                                                                                                        ChangedDate = (DateTime)x.Fields["System.ChangedDate"].Value,
                                                                                                        Type = x.Fields["System.WorkItemType"].Value as string,
                                                                                                        Fields = x.Fields.AsDictionary()
                                                                                                    }).ToDictionary(r => r.Number, r => r)) : null;
            context.Links = GetLinkData(workItem);
        }

        private static List<LinkItem> GetLinkData(WorkItem workItem)
        {
            var ls = new List<LinkItem>();

            foreach (Link l in workItem.Links)
            {
                if (l is Hyperlink)
                {
                    var lh = (Hyperlink)l;
                    ls.Add(new LinkItem() {
                        LinkType = LinkItemType.Hyperlink,
                        ArtifactLinkType = l.ArtifactLinkType.Name,
                        Comment = lh.Comment, 
                        LinkUri = lh.Location
                    });
                }
                else if (l is ExternalLink)
                {
                    var le = (ExternalLink)l;
                    ls.Add(new LinkItem() {
                        LinkType = LinkItemType.ExternalLink,
                        ArtifactLinkType = l.ArtifactLinkType.Name,
                        Comment = le.Comment,
                        LinkUri = le.LinkedArtifactUri
                    });
                }
                else if (l is RelatedLink)
                {
                    var lr = (RelatedLink)l;
                    ls.Add(new LinkItem()
                    {
                        LinkType = LinkItemType.RelatedLink,
                        ArtifactLinkType = l.ArtifactLinkType.Name,
                        Comment = lr.Comment,
                        RelatedWorkItem = lr.RelatedWorkItemId,
                        LinkTypeEndImmutableName = lr.LinkTypeEnd==null? "": lr.LinkTypeEnd.ImmutableName,
                        LinkTypeEndName = lr.LinkTypeEnd == null ? "" : lr.LinkTypeEnd.Name,
                    });
                } else
                {
                    Log.Debug("TfsExtensions::GetLinkData: RelatedLink is of ArtifactLinkType '{ArtifactLinkType}' and Type '{GetTypeName}' on WorkItemId: {WorkItemId}", l.ArtifactLinkType.Name, l.GetType().Name , workItem.Id);
                }
            }
            return ls;
        }

        public static void SaveToAzureDevOps(this WorkItemData context)
        {
            Log.Debug("TfsExtensions::SaveToAzureDevOps");

            if (context == null) throw new ArgumentNullException(nameof(context));
            var workItem = (WorkItem)context.internalObject;
            Log.Debug("TfsExtensions::SaveToAzureDevOps: ChangedBy: {ChangedBy}, AuthorisedBy: {AuthorizedIdentity}", workItem.ChangedBy, workItem.Store.TeamProjectCollection.AuthorizedIdentity.DisplayName);
            var fails = workItem.Validate();
            if (fails.Count > 0)
            {
                Log.Warning("Work Item is not ready to save as it has some invalid fields. This may not result in an error. Enable LogLevel as 'Debug' in the config to see more.");
                Log.Debug("--------------------------------------------------------------------------------------------------------------------");
                Log.Debug("--------------------------------------------------------------------------------------------------------------------");
                foreach (Field f in fails)
                {
                    Log.Debug("Invalid Field Object:\r\n{Field}", f.ToJson());
                }
                Log.Debug("--------------------------------------------------------------------------------------------------------------------");
                Log.Debug("--------------------------------------------------------------------------------------------------------------------");
            }
            Log.Verbose("TfsExtensions::SaveToAzureDevOps::Save()");
            workItem.Save();
            context.RefreshWorkItem();
        }

        public static string ToJson(this Field f)
        {
            Log.Debug("TfsExtensions::ToJson");
            dynamic expando = new ExpandoObject();
            expando.WorkItemId = f.WorkItem.Id;
            expando.CurrentRevisionWorkItemRev = f.WorkItem.Rev;
            expando.CurrentRevisionWorkItemTypeName = f.WorkItem.Type.Name;
            expando.Name = f.Name;
            expando.ReferenceName = f.ReferenceName;
            expando.Value = f.Value;
            expando.OriginalValue = f.OriginalValue;
            expando.ValueWithServerDefault = f.ValueWithServerDefault;

            expando.Status = f.Status;
            expando.IsRequired = f.IsRequired;
            expando.IsEditable = f.IsEditable;
            expando.IsDirty = f.IsDirty;
            expando.IsComputed = f.IsComputed;
            expando.IsChangedByUser = f.IsChangedByUser;
            expando.IsChangedInRevision = f.IsChangedInRevision;
            expando.HasPatternMatch = f.HasPatternMatch;

            expando.IsLimitedToAllowedValues = f.IsLimitedToAllowedValues;
            expando.HasAllowedValuesList = f.HasAllowedValuesList;
            expando.AllowedValues = f.AllowedValues;
            expando.IdentityFieldAllowedValues = f.IdentityFieldAllowedValues;
            expando.ProhibitedValues = f.ProhibitedValues;

            return JsonConvert.SerializeObject(expando, Formatting.Indented);
        }

        public static Project ToProject(this ProjectData projectdata)
        {
            if (!(projectdata.internalObject is Project))
            {
                throw new InvalidCastException($"The Work Item stored in the inner field must be of type {(nameof(Project))}");
            }
            return (Project)projectdata.internalObject;
        }

        public static ProjectData ToProjectData(this Project project)
        {
            var internalObject = new ProjectData
            {
                Id = project.Id.ToString(),
                Name = project.Name,
                internalObject = project
            };
            return internalObject;
        }

        public static WorkItem ToWorkItem(this WorkItemData workItemData)
        {
            if (!(workItemData.internalObject is WorkItem))
            {
                throw new InvalidCastException($"The Work Item stored in the inner field must be of type {(nameof(WorkItem))}");
            }
            return (WorkItem)workItemData.internalObject;
        }

        public static List<WorkItemData> ToWorkItemDataList(this IList<WorkItem> collection)
        {
            List<WorkItemData> list = new List<WorkItemData>();
            foreach (WorkItem wi in collection)
            {
                list.Add(wi.AsWorkItemData());
            }
            return list;
        }

        public static List<WorkItemData> ToWorkItemDataList(this WorkItemCollection collection)
        {
            List<WorkItemData> list = new List<WorkItemData>();
            foreach (WorkItem wi in collection)
            {
                list.Add(wi.AsWorkItemData());
            }
            return list;
        }
    }
}