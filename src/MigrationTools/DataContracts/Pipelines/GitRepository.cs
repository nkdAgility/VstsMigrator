using System;

namespace MigrationTools.DataContracts.Pipelines
{
    [ApiPath("git/repositories")]
    [ApiName("Git Repository")]
    public class GitRepository : RestApiDefinition
    {
        public Properties Properties { get; set; }

        public string Type { get; set; }

        public Uri Url { get; set; }

        public string DefaultBranch { get; set; }

        public bool Clean { get; set; }

        public bool CheckoutSubmodules { get; set; }

        public override bool HasTaskGroups()
        {
            return false;
        }

        public override bool HasVariableGroups()
        {
            return false;
        }

        public override void ResetObject()
        {
        }
    }
}