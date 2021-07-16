using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MigrationTools.DataContracts
{
    public class WorkItemData
    {
        // Using Get Only properties here and return the content of the FieldsCollection to make sure the correct data is returned in case

        // WorkItemData is used for the Revision of a WorkItem. It also seems to improve the initial query phase as less properties have to be set.
        // From a design point of view it maybe would be better to provide an Interface and have separate classes for WorkItemData and RevisionData.
        // This would make clearer what type of WorkItemData is in use at the moment.

        public WorkItemData()
        {
            Fields = new Dictionary<string, object>();
        }

        public Dictionary<string, object> Fields { get; set; }
        public string Id { get; set; }

        [JsonIgnore]
        public object internalObject { get; set; }

        public int Rev { get; set; }
        public DateTime ChangedDate { get; set; }

        public string ProjectName { get; set; }
        public SortedDictionary<int, RevisionItem> Revisions { get; set; }
        public string Title { get; set; }
        public string Type { get; set; }
        public List<LinkItem> Links { get; set; }

        //private object GetField(string field)
        //{
        //    if (Fields is null)
        //    {
        //        this.Fields = new Dictionary<string, object>();
        //    }
        //    if (!this.Fields.ContainsKey(field))
        //    {
        //        return default;
        //    }
        //    return this.Fields[field];
        //}

        //private void SetField<T>(string field, T value)
        //{
        //    if (Fields is null)
        //    {
        //        this.Fields = new Dictionary<string, object>();
        //    }
        //    if (this.Fields.ContainsKey(field))
        //    {
        //        this.Fields[field] = value;
        //    }
        //    else
        //    {
        //        this.Fields.Add(field, value);
        //    }
        //}
    }
}