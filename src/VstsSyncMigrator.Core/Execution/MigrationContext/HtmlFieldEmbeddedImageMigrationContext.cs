﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using VstsSyncMigrator.Engine.Configuration.Processing;

namespace VstsSyncMigrator.Engine
{
    public class HtmlFieldEmbeddedImageMigrationContext : MigrationContextBase
    {
        readonly HtmlFieldEmbeddedImageMigrationConfig _config;

        int current;
        int count = 0;
        int failures = 0;
        int updated = 0;
        int skipped = 0;

        public override string Name
        {
            get { return "HtmlFieldEmbeddedImageMigrationContext"; }
        }

        public HtmlFieldEmbeddedImageMigrationContext(MigrationEngine me, HtmlFieldEmbeddedImageMigrationConfig config)
            : base(me, config)
        {
            _config = config;
        }

        internal override void InternalExecute()
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            WorkItemStoreContext targetStore = new WorkItemStoreContext(me.Target, WorkItemStoreFlags.BypassRules);
            TfsQueryContext tfsqc = new TfsQueryContext(targetStore);
            tfsqc.AddParameter("TeamProject", me.Target.Name);
            tfsqc.Query = string.Format(@"SELECT [System.Id], [System.Tags] FROM WorkItems WHERE [System.TeamProject] = @TeamProject {0} ORDER BY [System.ChangedDate] desc", _config.QueryBit);
            WorkItemCollection targetWIS = tfsqc.Execute();
            Trace.WriteLine(string.Format("Found {0} work items...", targetWIS.Count), Name);

            current = targetWIS.Count;

            foreach (WorkItem targetWi in targetWIS)
            {
                Trace.WriteLine(string.Format("{0} - Fixing: {1}-{2}", current, targetWi.Id, targetWi.Type.Name), Name);

                // Deside on WIT
                if (me.WorkItemTypeDefinitions.ContainsKey(targetWi.Type.Name))
                {
                    FixHtmlAttachmentLinks(targetWi, me.Source.Collection.Uri.ToString(), me.Target.Collection.Uri.ToString());
                }
                else
                {
                    Trace.WriteLine("...not supported", Name);
                    skipped++;
                }

                current--;
                count++;

                Trace.Flush();
            }
            //////////////////////////////////////////////////
            stopwatch.Stop();
            Trace.WriteLine(string.Format(@"DONE in {0:%h} hours {0:%m} minutes {0:s\:fff} seconds - {1} Items, {2} Updated, {3} Skipped, {4} Failures", stopwatch.Elapsed, targetWIS.Count, updated, skipped, failures), this.Name);
        }


        /**
         *  from https://gist.github.com/pietergheysens/792ed505f09557e77ddfc1b83531e4fb
         */
        private void FixHtmlAttachmentLinks(WorkItem wi, string oldTfsurl, string newTfsurl)
        {
            bool wiUpdated = false;
            string oldTfsurl2;
            Uri sourceUrl = new Uri(oldTfsurl);
            if (sourceUrl.Scheme == Uri.UriSchemeHttp)
                oldTfsurl2 = new UriBuilder("https", sourceUrl.Host + sourceUrl.AbsolutePath).ToString();
            else
                oldTfsurl2 = new UriBuilder("http", sourceUrl.Host + sourceUrl.AbsolutePath).ToString();

            string regExSearchForImageUrl = "(?<=<img.*src=\")[^\"]*";

            foreach (Field field in wi.Fields)
            {
                if (field.FieldDefinition.FieldType == FieldType.Html)
                {
                    MatchCollection matches = Regex.Matches((string) field.Value, regExSearchForImageUrl);

                    string regExSearchFileName = "(?<=FileName=)[^=]*";
                    foreach (Match match in matches)
                    {
                        //todo server aliases....
                        if (match.Value.Contains(oldTfsurl) || match.Value.Contains(oldTfsurl2) || match.Value.Contains("http://server01-tfs15:8080"))
                        {
                            //save image locally and upload as attachment
                            Match newFileNameMatch = Regex.Match(match.Value, regExSearchFileName);
                            if (newFileNameMatch.Success)
                            {
                                Trace.WriteLine(String.Format("field '{0}' has match: {1}", field.Name, match.Value));
                                string fullImageFilePath = Path.GetTempPath() + newFileNameMatch.Value;

                                var webClient = new WebClient();

                                // When alternate credentials are given, use basic authentication with the given credentials
                                if (!String.IsNullOrWhiteSpace(_config.AlternateCredentialsUsername) &&
                                    !String.IsNullOrWhiteSpace(_config.AlternateCredentialsPassword))
                                {
                                    string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes(_config.AlternateCredentialsUsername + ":" + _config.AlternateCredentialsPassword));
                                    webClient.Headers[HttpRequestHeader.Authorization] = string.Format("Basic {0}", credentials);
                                }
                                else
                                {
                                    webClient.UseDefaultCredentials = true;
                                }

                                webClient.DownloadFile(match.Value, fullImageFilePath);

                                int attachmentIndex = wi.Attachments.Add(new Attachment(fullImageFilePath));
                                wi.Save();
                                string attachmentGuid = wi.Attachments[attachmentIndex].FileGuid;

                                string newImageLink =
                                    String.Format(
                                        "{0}/WorkItemTracking/v1.0/AttachFileHandler.ashx?FileNameGuid={1}&amp;FileName={2}",
                                        newTfsurl, attachmentGuid, newFileNameMatch.Value);

                                field.Value = field.Value.ToString().Replace(match.Value, newImageLink);
                                wi.Attachments.RemoveAt(attachmentIndex);
                                wi.Save();
                                wiUpdated = true;
                            }
                        }
                    }
                }
            }

            if (wiUpdated)
                updated++;
        }

    }
}

