﻿using System;
using System.Collections.Generic;
using MigrationTools.Endpoints;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MigrationTools.DataContracts
{
    public class WorkItemData2
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public EndpointDirection Direction { get; set; }

        public string Id { get; set; }
        public string Type { get; set; }

        [JsonIgnoreAttribute]
        public int LatestRevisionNumber
        {
            get
            {
                return Revisions[Revisions.Count - 1].Number;
            }
        }

        [JsonIgnoreAttribute]
        public RevisionItem2 LatestRevision
        {
            get
            {
                return Revisions[Revisions.Count - 1];
            }
        }

        [JsonIgnoreAttribute]
        public DateTime LatestRevDate
        {
            get
            {
                return Revisions[Revisions.Count - 1].ChangedDate;
            }
        }

        public List<RevisionItem2> Revisions { get; set; }
    }
}