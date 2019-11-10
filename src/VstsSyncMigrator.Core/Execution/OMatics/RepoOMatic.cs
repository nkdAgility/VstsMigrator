﻿using Microsoft.TeamFoundation;
using Microsoft.TeamFoundation.Git.Client;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VstsSyncMigrator.Engine;

namespace VstsSyncMigrator.Core.Execution.OMatics
{
   public class RepoOMatic
    {
        MigrationEngine migrationEngine;
        GitRepositoryService sourceRepoService;
        IList<GitRepository> sourceRepos;
        GitRepositoryService targetRepoService;
        IList<GitRepository> targetRepos;
        List<string> wits;

        public RepoOMatic(MigrationEngine me)
        {
            migrationEngine = me;
            sourceRepoService = me.Source.Collection.GetService<GitRepositoryService>();
            sourceRepos = sourceRepoService.QueryRepositories(me.Source.Config.Project);
            //////////////////////////////////////////////////
            targetRepoService = me.Target.Collection.GetService<GitRepositoryService>();
            targetRepos = targetRepoService.QueryRepositories(me.Target.Config.Project);
            wits = new List<string>
                {
                    "Branch",
                    "Fixed in Commit",
                    "Pull Request",
                    "Fixed in Changeset"    //TFVC
                };
        }

        public int FixExternalLinks(WorkItem targetWorkItem, WorkItemStoreContext targetStore, WorkItem sourceWorkItem, bool save = true)
        {
            List<ExternalLink> newEL = new List<ExternalLink>();
            List<ExternalLink> removeEL = new List<ExternalLink>();
            int count = 0;
            foreach (Link l in targetWorkItem.Links)
            {
                if (l is ExternalLink && wits.Contains(l.ArtifactLinkType.Name))
                {
                    ExternalLink el = (ExternalLink) l;
                    GitRepositoryInfo sourceRepoInfo = GitRepositoryInfo.Create(el, sourceRepos, migrationEngine, sourceWorkItem?.Project?.Name);                    

                    if (sourceRepoInfo != null)
                    {
                        string targetRepoName = GetTargetRepoName(migrationEngine.GitRepoMappings, sourceRepoInfo);
                        string targetProjectName = migrationEngine.Target.Config.Project;
                        GitRepositoryInfo targetRepoInfo = GitRepositoryInfo.Create(targetRepoName, sourceRepoInfo, migrationEngine, targetRepos);
               
                        // Fix commit links if target repo has been found
                        if (targetRepoInfo != null)
                        {
                            Trace.WriteLine($"Fixing {sourceRepoInfo.GitRepo?.RemoteUrl} to {targetRepoInfo.GitRepo.RemoteUrl}?");

                            // Create External Link object
                            ExternalLink newLink = null;
                            switch (l.ArtifactLinkType.Name)
                            {
                                case "Branch":
                                    newLink = new ExternalLink(targetStore.Store.RegisteredLinkTypes[ArtifactLinkIds.Branch],
                                        $"vstfs:///git/ref/{targetRepoInfo.GitRepo.ProjectReference.Id}%2f{targetRepoInfo.GitRepo.Id}%2f{sourceRepoInfo.CommitID}");
                                    break;

                                case "Fixed in Changeset":
                                case "Fixed in Commit":
                                    newLink = new ExternalLink(targetStore.Store.RegisteredLinkTypes[ArtifactLinkIds.Commit],
                                        $"vstfs:///git/commit/{targetRepoInfo.GitRepo.ProjectReference.Id}%2f{targetRepoInfo.GitRepo.Id}%2f{sourceRepoInfo.CommitID}");
                                    break;
                                case "Pull Request":
                                    //newLink = new ExternalLink(targetStore.Store.RegisteredLinkTypes[ArtifactLinkIds.PullRequest],
                                    //    $"vstfs:///Git/PullRequestId/{targetRepoInfo.GitRepo.ProjectReference.Id}%2f{targetRepoInfo.GitRepo.Id}%2f{sourceRepoInfo.CommitID}");
                                    removeEL.Add(el);
                                    break;

                                default:
                                    Trace.WriteLine(String.Format("Skipping unsupported link type {0}", l.ArtifactLinkType.Name));
                                    break;
                            }

                            if (newLink != null)
                            {
                                var elinks = from Link lq in targetWorkItem.Links
                                             where wits.Contains(lq.ArtifactLinkType.Name)
                                             select (ExternalLink)lq;
                                var found =
                                (from Link lq in elinks
                                 where (((ExternalLink)lq).LinkedArtifactUri.ToLower() == newLink.LinkedArtifactUri.ToLower())
                                 select lq).SingleOrDefault();
                                if (found == null)
                                {
                                    newEL.Add(newLink);
                                }
                                removeEL.Add(el);
                            }
                        }
                        else
                        {
                            Trace.WriteLine($"FAIL: cannot map {sourceRepoInfo.GitRepo?.RemoteUrl} to ???");
                        }
                    }
                    else
                    {
                        Trace.WriteLine($"FAIL could not find source git repo when trying to fix external link");
                    }
                }
            }
            // add and remove
            foreach (ExternalLink eln in newEL)
            {
                try
                {
                    Trace.WriteLine("Adding " + eln.LinkedArtifactUri);
                    targetWorkItem.Links.Add(eln);

                }
                catch (Exception)
                {

                    // eat exception as sometimes TFS thinks this is an attachment
                }
            }
            foreach (ExternalLink elr in removeEL)
            {
                if (targetWorkItem.Links.Contains(elr))
                {
                    try
                    {
                        Trace.WriteLine("Removing " + elr.LinkedArtifactUri);
                        targetWorkItem.Links.Remove(elr);
                        count++;
                    }
                    catch (Exception)
                    {

                        // eat exception as sometimes TFS thinks this is an attachment
                    }
                }

            }

            if (targetWorkItem.IsDirty && save)
            {
                Trace.WriteLine($"Saving {targetWorkItem.Id}");
                targetWorkItem.Fields["System.ChangedBy"].Value = "Migration";
                targetWorkItem.Save();
            }
            return count;

        }

        private string GetTargetRepoName(Dictionary<string, string> gitRepoMappings, GitRepositoryInfo repoInfo)
        {
            if (gitRepoMappings.ContainsKey(repoInfo.GitRepo.Name))
            {
                return gitRepoMappings[repoInfo.GitRepo.Name];
            } else
            {
                return repoInfo.GitRepo.Name;
            }
        }
    }

    public class GitRepositoryInfo
    {
        public string CommitID { get; }
        public GitRepository GitRepo { get; }

        public GitRepositoryInfo(string CommitID, GitRepository GitRepo)
        {
            this.CommitID = CommitID;
            this.GitRepo = GitRepo;            
        }

        public static GitRepositoryInfo Create(ExternalLink gitExternalLink, IList<GitRepository> possibleRepos, MigrationEngine migrationEngine, string workItemSourceProjectName)
        {
            var repoType = DetermineFromLink(gitExternalLink.LinkedArtifactUri);
            switch (repoType)
            {
                case RepistoryType.Git:
                    return CreateFromGit(gitExternalLink, possibleRepos);
                
                case RepistoryType.TFVC:                    
                    return CreateFromTFVC(gitExternalLink, possibleRepos, migrationEngine.ChangeSetMapping, migrationEngine.Source.Config.Project, workItemSourceProjectName);
            }

            return null;
        }

        private static GitRepositoryInfo CreateFromTFVC(ExternalLink gitExternalLink, IList<GitRepository> possibleRepos, 
            Dictionary<int, string> changesetMapping, string sourceProjectName, string workItemSourceProjectName)
        {
            string commitID;
            string repoID;
            GitRepository gitRepo;

            //vstfs:///VersionControl/Changeset/{id}
            var changeSetIdPart = gitExternalLink.LinkedArtifactUri.Substring(gitExternalLink.LinkedArtifactUri.LastIndexOf('/') + 1);
            if (!int.TryParse(changeSetIdPart, out int changeSetId))
            {
                return null;
            }

            var commitIDKvPair = changesetMapping.FirstOrDefault(item => item.Key == changeSetId);
            if (string.IsNullOrEmpty(commitIDKvPair.Value))
            {
                Trace.WriteLine($"Commit Id not found from Changeset Id {changeSetIdPart}.");
                return null;
            }

            //assume the GitRepository source name is the work items project name, which changeset links needs to be fixed
            return new GitRepositoryInfo(commitIDKvPair.Value, new GitRepository() { Name = workItemSourceProjectName });
        }

        private static GitRepositoryInfo CreateFromGit(ExternalLink gitExternalLink, IList<GitRepository> possibleRepos)
        {
            string commitID;
            string repoID;
            GitRepository gitRepo;
            //vstfs:///Git/Commit/25f94570-e3e7-4b79-ad19-4b434787fd5a%2f50477259-3058-4dff-ba4c-e8c179ec5327%2f41dd2754058348d72a6417c0615c2543b9b55535
            string guidbits = gitExternalLink.LinkedArtifactUri.Substring(gitExternalLink.LinkedArtifactUri.LastIndexOf('/') + 1);
            string[] bits = Regex.Split(guidbits, "%2f", RegexOptions.IgnoreCase);
            repoID = bits[1];
            if (bits.Count() >= 3)
            {
                commitID = $"{bits[2]}";
                for (int i = 3; i < bits.Count(); i++)
                {
                    commitID += $"%2f{bits[i]}";
                }
            }
            else
            {
                commitID = bits[2];
            }
            gitRepo =
                (from g in possibleRepos where g.Id.ToString() == repoID select g)
                .SingleOrDefault();
            return new GitRepositoryInfo(commitID, gitRepo);
        }

        private enum RepistoryType
        {
            Unknown,
            TFVC,
            Git
        }

        private static RepistoryType DetermineFromLink(string link)
        {
            if (string.IsNullOrEmpty(link))
                throw new ArgumentNullException("link");

            //vstfs:///Git/Commit/25f94570-e3e7-4b79-ad19-4b434787fd5a%2f50477259-3058-4dff-ba4c-e8c179ec5327%2f41dd2754058348d72a6417c0615c2543b9b55535
            if (link.ToLowerInvariant().Contains("git/commit"))
                return RepistoryType.Git;

            //vstfs:///VersionControl/Changeset/{id}
            if (link.ToLowerInvariant().Contains("versioncontrol/changeset"))
                return RepistoryType.TFVC;

            Trace.WriteLine($"Cannot determine repository type from external link: {link}");

            return RepistoryType.Unknown;
        }

        internal static GitRepositoryInfo Create(string targetRepoName, GitRepositoryInfo sourceRepoInfo , MigrationEngine migrationEngine, IList<GitRepository> targetRepos)
        {
            GitRepository gitRepo;
            // Source and Target project names match
            if (migrationEngine.Source.Config.Project == migrationEngine.Target.Config.Project)
            {
                gitRepo = (from g in targetRepos
                              where
                              g.Name == targetRepoName &&
                              g.ProjectReference.Name == migrationEngine.Source.Config.Project
                              select g).SingleOrDefault();
            }
            // Source and Target project names do not match
            else
            {
                gitRepo = (from g in targetRepos
                              where
                              g.Name == targetRepoName &&
                              g.ProjectReference.Name != migrationEngine.Source.Config.Project
                              select g).SingleOrDefault();
            }
            return new GitRepositoryInfo(sourceRepoInfo.CommitID, gitRepo);
        }
    }
}
