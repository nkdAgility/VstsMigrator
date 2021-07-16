using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using MigrationTools._EngineV1.Clients;
using MigrationTools.DataContracts;
using MigrationTools.Exceptions;
using MigrationTools.Processors;

namespace MigrationTools.Enrichers
{
    public class TfsWorkItemLinkEnricher : WorkItemProcessorEnricher
    {
        private bool _save = true;
        private bool _filterWorkItemsThatAlreadyExistInTarget = true;
        private IMigrationEngine Engine;

        public TfsWorkItemLinkEnricher(IServiceProvider services, ILogger<TfsWorkItemLinkEnricher> logger) : base(services, logger)
        {
            Engine = services.GetRequiredService<IMigrationEngine>();
        }

        [Obsolete]
        public override void Configure(
            bool save = true,
            bool filterWorkItemsThatAlreadyExistInTarget = true)
        {
            _save = save;
            _filterWorkItemsThatAlreadyExistInTarget = filterWorkItemsThatAlreadyExistInTarget;
        }

        [Obsolete]
        public override int Enrich(WorkItemData sourceWorkItemLinkStart, WorkItemData targetWorkItemLinkStart)
        {
            if (sourceWorkItemLinkStart is null)
            {
                throw new ArgumentNullException(nameof(sourceWorkItemLinkStart));
            }
            if (targetWorkItemLinkStart is null)
            {
                throw new ArgumentNullException(nameof(targetWorkItemLinkStart));
            }
            if (targetWorkItemLinkStart.Id == "0")
            {
                Log.LogWarning("TfsWorkItemLinkEnricher::Enrich: Target work item must be saved before you can add a link: exiting Link Migration");
                return 0;
            }

            if (ShouldCopyLinks(sourceWorkItemLinkStart, targetWorkItemLinkStart))
            {
                Log.LogDebug("Links = '{@sourceWorkItemLinkStartLinks}", sourceWorkItemLinkStart.Links);
                foreach (Link item in sourceWorkItemLinkStart.ToWorkItem().Links)
                {
                    try
                    {
                        Log.LogInformation("Migrating link for {sourceWorkItemLinkStartId} of type {ItemGetTypeName}", sourceWorkItemLinkStart.Id, item.GetType().Name);
                        if (IsHyperlink(item))
                        {
                            CreateHyperlink((Hyperlink)item, targetWorkItemLinkStart);
                        }
                        else if (IsRelatedLink(item))
                        {
                            RelatedLink rl = (RelatedLink)item;
                            CreateRelatedLink(sourceWorkItemLinkStart, rl, targetWorkItemLinkStart);
                        }
                        else if (IsExternalLink(item))
                        {
                            var el = (ExternalLink)item;
                            if (!IsBuildLink(el))
                            {
                                CreateExternalLink(el, targetWorkItemLinkStart);
                            }
                        }
                        else
                        {
                            UnknownLinkTypeException ex = new UnknownLinkTypeException(string.Format("  [UnknownLinkType] Unable to {0}", item.GetType().Name));
                            Log.LogError(ex, "LinkMigrationContext");
                            throw ex;
                        }
                    }
                    catch (WorkItemLinkValidationException ex)
                    {
                        sourceWorkItemLinkStart.ToWorkItem().Reset();
                        targetWorkItemLinkStart.ToWorkItem().Reset();
                        Log.LogError(ex, "[WorkItemLinkValidationException] Adding link for wiSourceL={sourceWorkItemLinkStartId}", sourceWorkItemLinkStart.Id);
                    }
                    catch (FormatException ex)
                    {
                        sourceWorkItemLinkStart.ToWorkItem().Reset();
                        targetWorkItemLinkStart.ToWorkItem().Reset();
                        Log.LogError(ex, "[CREATE-FAIL] Adding Link for wiSourceL={sourceWorkItemLinkStartId}", sourceWorkItemLinkStart.Id);
                    }
                    catch (UnexpectedErrorException ex)
                    {
                        sourceWorkItemLinkStart.ToWorkItem().Reset();
                        targetWorkItemLinkStart.ToWorkItem().Reset();
                        Log.LogError(ex, "[UnexpectedErrorException] Adding Link for wiSourceL={sourceWorkItemLinkStartId}", sourceWorkItemLinkStart.Id);
                    }
                }
            }
            if (sourceWorkItemLinkStart.Type == "Test Case")
            {
                MigrateSharedSteps(sourceWorkItemLinkStart, targetWorkItemLinkStart);
            }
            return 0;
        }

        private void MigrateSharedSteps(WorkItemData wiSourceL, WorkItemData wiTargetL)
        {
            const string microsoftVstsTcmSteps = "Microsoft.VSTS.TCM.Steps";
            var oldSteps = wiTargetL.ToWorkItem().Fields[microsoftVstsTcmSteps].Value.ToString();
            var newSteps = oldSteps;

            var sourceSharedStepLinks = wiSourceL.ToWorkItem().Links.OfType<RelatedLink>()
                .Where(x => x.LinkTypeEnd.Name == "Shared Steps").ToList();
            var sourceSharedSteps =
                sourceSharedStepLinks.Select(x => Engine.Source.WorkItems.GetWorkItem(x.RelatedWorkItemId.ToString()));

            foreach (WorkItemData sourceSharedStep in sourceSharedSteps)
            {
                WorkItemData matchingTargetSharedStep =
                    Engine.Target.WorkItems.FindReflectedWorkItemByReflectedWorkItemId(sourceSharedStep);

                if (matchingTargetSharedStep != null)
                {
                    newSteps = newSteps.Replace($"ref=\"{sourceSharedStep.Id}\"",
                        $"ref=\"{matchingTargetSharedStep.Id}\"");
                    wiTargetL.ToWorkItem().Fields[microsoftVstsTcmSteps].Value = newSteps;
                }
            }

            if (wiTargetL.ToWorkItem().IsDirty && _save)
            {
                wiTargetL.SaveToAzureDevOps();
            }
        }

        private void CreateExternalLink(ExternalLink sourceLink, WorkItemData target)
        {
            var exist = (from Link l in target.ToWorkItem().Links
                         where l is ExternalLink && ((ExternalLink)l).LinkedArtifactUri == ((ExternalLink)sourceLink).LinkedArtifactUri
                         select (ExternalLink)l).SingleOrDefault();
            if (exist == null)
            {
                Log.LogInformation("Creating new {SourceLinkType} on {TargetId}", sourceLink.GetType().Name, target.Id);
                ExternalLink el = new ExternalLink(sourceLink.ArtifactLinkType, sourceLink.LinkedArtifactUri)
                {
                    Comment = sourceLink.Comment
                };
                target.ToWorkItem().Links.Add(el);
                if (_save)
                {
                    try
                    {
                        target.SaveToAzureDevOps();
                    }
                    catch (Exception ex)
                    {
                        // Ignore this link because the TFS server didn't recognize its type (There's no point in crashing the rest of the migration due to a link)
                        if(ex.Message.Contains("Unrecognized Resource link"))
                        {
                            Log.LogError(ex, "[{ExceptionType}] Failed to save link {SourceLinkType} on {TargetId}", ex.GetType().Name, sourceLink.GetType().Name, target.Id);
                            // Remove the link from the target so it doesn't cause problems downstream
                            target.ToWorkItem().Links.Remove(el);
                        }
                        else
                        {
                            //pass along the exception since we don't know what went wrong
                            throw;
                        }
                    }
                }
            }
            else
            {
                Log.LogInformation("Link {SourceLinkType} on {TargetId} already exists",
                                                  sourceLink.GetType().Name, target.Id);
            }
        }

        private bool IsExternalLink(Link item)
        {
            return item is ExternalLink;
        }

        private bool IsBuildLink(ExternalLink link)
        {
            return link.LinkedArtifactUri != null &&
                   link.LinkedArtifactUri.StartsWith("vstfs:///Build/Build/", StringComparison.InvariantCultureIgnoreCase);
        }

        private void CreateRelatedLink(WorkItemData wiSourceL, RelatedLink item, WorkItemData wiTargetL)
        {
            RelatedLink rl = (RelatedLink)item;
            WorkItemData wiSourceR = null;
            WorkItemData wiTargetR = null;

            Log.LogDebug("RelatedLink is of ArtifactLinkType='{ArtifactLinkType}':LinkTypeEnd='{LinkTypeEndImmutableName}' on WorkItemId s:{ids} t:{idt}", rl.ArtifactLinkType.Name, rl.LinkTypeEnd == null? "null" : rl.LinkTypeEnd.ImmutableName, wiSourceL.Id, wiTargetL.Id);

            if (rl.LinkTypeEnd != null) // On a registered link type these will for sure fail as target is not in the system.
            {
                try
                {
                    wiSourceR = Engine.Source.WorkItems.GetWorkItem(rl.RelatedWorkItemId.ToString());
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "  [FIND-FAIL] Adding Link of type {0} where wiSourceL={1}, wiTargetL={2} ", rl.LinkTypeEnd.ImmutableName, wiSourceL.Id, wiTargetL.Id);
                    return;
                }
                try
                {
                    wiTargetR = GetRightHandSideTargetWi(wiSourceL, wiSourceR, wiTargetL);
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "  [FIND-FAIL] Adding Link of type {0} where wiSourceL={1}, wiTargetL={2} ", rl.LinkTypeEnd.ImmutableName, wiSourceL.Id, wiTargetL.Id);
                    return;
                }
            }
            
            if (wiTargetR != null)
            {
                bool IsExisting = false;
                try
                {
                    var exist = (
                        from Link l in wiTargetL.ToWorkItem().Links
                        where l is RelatedLink
                            && ((RelatedLink)l).RelatedWorkItemId.ToString() == wiTargetR.Id
                            && ((RelatedLink)l).LinkTypeEnd.ImmutableName == item.LinkTypeEnd.ImmutableName
                        select (RelatedLink)l).SingleOrDefault();
                    IsExisting = (exist != null);
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "  [SKIP] Unable to migrate links where wiSourceL={0}, wiSourceR={1}, wiTargetL={2}", ((wiSourceL != null) ? wiSourceL.Id.ToString() : "NotFound"), ((wiSourceR != null) ? wiSourceR.Id.ToString() : "NotFound"), ((wiTargetL != null) ? wiTargetL.Id.ToString() : "NotFound"));
                    return;
                }

                if (!IsExisting && !wiTargetR.ToWorkItem().IsAccessDenied)
                {
                    if (wiSourceR.Id != wiTargetR.Id)
                    {
                        Log.LogInformation("  [CREATE-START] Adding Link of type {0} where wiSourceL={1}, wiSourceR={2}, wiTargetL={3}, wiTargetR={4} ", rl.LinkTypeEnd.ImmutableName, wiSourceL.Id, wiSourceR.Id, wiTargetL.Id, wiTargetR.Id);
                        var client = (TfsWorkItemMigrationClient)Engine.Target.WorkItems;
                        if (!client.Store.WorkItemLinkTypes.LinkTypeEnds.Contains(rl.LinkTypeEnd.ImmutableName))
                        {
                            Log.LogError($"  [SKIP] Unable to migrate Link because type {rl.LinkTypeEnd.ImmutableName} does not exist in the target project.");
                            return;
                        }

                        WorkItemLinkTypeEnd linkTypeEnd = client.Store.WorkItemLinkTypes.LinkTypeEnds[rl.LinkTypeEnd.ImmutableName];
                        RelatedLink newRl = new RelatedLink(linkTypeEnd, int.Parse(wiTargetR.Id));
                        if (linkTypeEnd.ImmutableName == "System.LinkTypes.Hierarchy-Forward")
                        {
                            var potentialParentConflictLink = ( // TF201036: You cannot add a Child link between work items xxx and xxx because a work item can have only one Parent link.
                                    from Link l in wiTargetR.ToWorkItem().Links
                                    where l is RelatedLink
                                        && ((RelatedLink)l).LinkTypeEnd.ImmutableName == "System.LinkTypes.Hierarchy-Reverse"
                                    select (RelatedLink)l).SingleOrDefault();
                            if (potentialParentConflictLink != null)
                            {
                                wiTargetR.ToWorkItem().Links.Remove(potentialParentConflictLink);
                            }
                            linkTypeEnd = ((TfsWorkItemMigrationClient)Engine.Target.WorkItems).Store.WorkItemLinkTypes.LinkTypeEnds["System.LinkTypes.Hierarchy-Reverse"];
                            RelatedLink newLl = new RelatedLink(linkTypeEnd, int.Parse(wiTargetL.Id));
                            wiTargetR.ToWorkItem().Links.Add(newLl);
                            wiTargetR.ToWorkItem().Fields["System.ChangedBy"].Value = "Migration";
                            wiTargetR.SaveToAzureDevOps();
                        }
                        else
                        {
                            if (linkTypeEnd.ImmutableName == "System.LinkTypes.Hierarchy-Reverse")
                            {
                                var potentialParentConflictLink = ( // TF201065: You can not add a Parent link to this work item because a work item can have only one link of this type.
                                    from Link l in wiTargetL.ToWorkItem().Links
                                    where l is RelatedLink
                                        && ((RelatedLink)l).LinkTypeEnd.ImmutableName == "System.LinkTypes.Hierarchy-Reverse"
                                    select (RelatedLink)l).SingleOrDefault();
                                if (potentialParentConflictLink != null)
                                {
                                    wiTargetL.ToWorkItem().Links.Remove(potentialParentConflictLink);
                                }
                            }
                            wiTargetL.ToWorkItem().Links.Add(newRl);
                            if (_save)
                            {
                                wiTargetL.SaveToAzureDevOps();
                            }
                        }
                        Log.LogInformation(
                                "  [CREATE-SUCCESS] Adding Link of type {0} where wiSourceL={1}, wiSourceR={2}, wiTargetL={3}, wiTargetR={4} ",
                                rl.LinkTypeEnd.ImmutableName, wiSourceL.Id, wiSourceR.Id, wiTargetL.Id, wiTargetR.Id);
                    }
                    else
                    {
                        Log.LogInformation(
                                  "  [SKIP] Unable to migrate link where Link of type {0} where wiSourceL={1}, wiSourceR={2}, wiTargetL={3}, wiTargetR={4} as target WI has not been migrated",
                                  rl.LinkTypeEnd.ImmutableName, wiSourceL.Id, wiSourceR.Id, wiTargetL.Id, wiTargetR.Id);
                    }
                }
                else
                {
                    if (IsExisting)
                    {
                        Log.LogInformation("  [SKIP] Already Exists a Link of type {0} where wiSourceL={1}, wiSourceR={2}, wiTargetL={3}, wiTargetR={4} ", rl.LinkTypeEnd.ImmutableName, wiSourceL.Id, wiSourceR.Id, wiTargetL.Id, wiTargetR.Id);
                    }
                    if (wiTargetR.ToWorkItem().IsAccessDenied)
                    {
                        Log.LogInformation("  [AccessDenied] The Target  work item is inaccessable to create a Link of type {0} where wiSourceL={1}, wiSourceR={2}, wiTargetL={3}, wiTargetR={4} ", rl.LinkTypeEnd.ImmutableName, wiSourceL.Id, wiSourceR.Id, wiTargetL.Id, wiTargetR.Id);
                    }
                }
            }
            else
            {
                Log.LogWarning("[SKIP] [LINK_CAPTURE_RELATED] [{RegisteredLinkType}] target not found. wiSourceL={wiSourceL}, wiSourceR={wiSourceR}, wiTargetL={wiTargetL}", rl.ArtifactLinkType.GetType().Name, wiSourceL == null ? "null" : wiSourceL.Id , wiSourceR == null ? "null" : wiSourceR.Id, wiTargetL == null? "null": wiTargetL.Id);
            }
        }

        private WorkItemData GetRightHandSideTargetWi(WorkItemData wiSourceL, WorkItemData wiSourceR, WorkItemData wiTargetL)
        {
            WorkItemData wiTargetR;
            if (!(wiTargetL == null)
                && wiSourceR.ToWorkItem().Project.Name == wiTargetL.ToWorkItem().Project.Name
                && wiSourceR.ToWorkItem().Project.Store.TeamProjectCollection.Uri.ToString().Replace("/", "") == wiTargetL.ToWorkItem().Project.Store.TeamProjectCollection.Uri.ToString().Replace("/", ""))
            {
                // Moving to same team project as SourceR
                wiTargetR = wiSourceR;
            }
            else
            {
                // Moving to Other Team Project from Source
                wiTargetR = Engine.Target.WorkItems.FindReflectedWorkItem(wiSourceR, true);
                if (wiTargetR == null) // Assume source only (other team project)
                {
                    wiTargetR = wiSourceR;
                    if (wiTargetR.ToWorkItem().Project.Store.TeamProjectCollection.Uri.ToString().Replace("/", "") != wiSourceR.ToWorkItem().Project.Store.TeamProjectCollection.Uri.ToString().Replace("/", ""))
                    {
                        wiTargetR = null; // Totally bogus break! as not same team collection
                    }
                }
            }
            return wiTargetR;
        }

        private bool IsRelatedLink(Link item)
        {
            return item is RelatedLink;
        }

        private void CreateHyperlink(Hyperlink sourceLink, WorkItemData target)
        {
            var sourceLinkAbsoluteUri = GetAbsoluteUri(sourceLink);
            if (string.IsNullOrEmpty(sourceLinkAbsoluteUri))
            {
                Log.LogWarning($"  [SKIP] Unable to create a hyperlink to [{sourceLink.Location}]");
                return;
            }

            var exist = (from hyperlink in target.ToWorkItem().Links.Cast<Link>().Where(l => l is Hyperlink).Cast<Hyperlink>()
                         let absoluteUri = GetAbsoluteUri(hyperlink)
                         where sourceLinkAbsoluteUri == absoluteUri
                         select hyperlink).SingleOrDefault();

            if (exist != null)
            {
                return;
            }

            var hl = new Hyperlink(sourceLinkAbsoluteUri) // Use AbsoluteUri here as a possible \\UNC\Path\Link will be converted to file://UNC/Path/Link this way
            {
                Comment = sourceLink.Comment
            };

            target.ToWorkItem().Links.Add(hl);
            if (_save)
            {
                target.SaveToAzureDevOps();
            }
        }

        private string GetAbsoluteUri(Hyperlink hyperlink)
        {
            try
            {
                return new Uri(hyperlink.Location.Trim('"')).AbsoluteUri;
            }
            catch (UriFormatException e)
            {
                Log.LogError($"Unable to get AbsoluteUri of [{hyperlink.Location}]: {e.Message}");
                return null;
            }
        }

        private bool ShouldCopyLinks(WorkItemData sourceWorkItemLinkStart, WorkItemData targetWorkItemLinkStart)
        {
            if (_filterWorkItemsThatAlreadyExistInTarget)
            {
                if (targetWorkItemLinkStart.ToWorkItem().Links.Count == sourceWorkItemLinkStart.ToWorkItem().Links.Count) // we should never have this as the target should not have existed in this path
                {
                    Log.LogInformation("[SKIP] Source and Target have same number of links  {sourceWorkItemLinkStartId} - {sourceWorkItemLinkStartType}", sourceWorkItemLinkStart.Id, sourceWorkItemLinkStart.Type.ToString());
                    return false;
                }
            }
            return true;
        }

        private bool IsHyperlink(Link item)
        {
            return item is Hyperlink;
        }

        [Obsolete("v2 Archtecture: use Configure(bool save = true, bool filter = true) instead", true)]
        public override void Configure(IProcessorEnricherOptions options)
        {
            throw new NotImplementedException();
        }

        protected override void RefreshForProcessorType(IProcessor processor)
        {
            throw new NotImplementedException();
        }

        protected override void EntryForProcessorType(IProcessor processor)
        {
            throw new NotImplementedException();
        }
    }
}