using Newtonsoft.Json.Linq;
using Redmine.Net.Api.Types;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace redminerelations
{
    class Program
    {
        static Redmine.Net.Api.RedmineManager redmine = new Redmine.Net.Api.RedmineManager(redminemigration.Properties.Settings.Default.redmine_uri, redminemigration.Properties.Settings.Default.redmine_apikey);
        static readonly string project_id = redminemigration.Properties.Settings.Default.redmine_projectid;
        static JObject correlationmap = new JObject();
        static readonly string mapfilename = $"../../../redminemigration/bin/Debug/correlationmap_{redminemigration.Properties.Settings.Default.vsts_projectname}.json";

        static void Main(string[] args)
        {
            if (System.IO.File.Exists(mapfilename))
                correlationmap = JObject.Parse(System.IO.File.ReadAllText(mapfilename));

            var issues = redmine.GetObjects<Issue>(new System.Collections.Specialized.NameValueCollection { { "project_id", project_id }, { "status_id", "*" }, { "sort", "id" }, { "include", "relations" } });

            // parents
            var withparents = issues.Where(x => x.ParentIssue != null);
            foreach (var withparent in withparents)
            {
                var vstsparent = correlationmap[withparent.ParentIssue.Id.ToString()];
                var vstschild = correlationmap[withparent.Id.ToString()];

                if (vstsparent != null & vstschild != null)
                {
                    var client = new RestClient($"{redminemigration.Properties.Settings.Default.vsts_uri}/_apis/wit/workitems/{vstschild}?api-version=4.1");
                    var request = new RestRequest(Method.PATCH);
                    request.AddHeader("Authorization", redminemigration.Properties.Settings.Default.vsts_authorizationheader);
                    request.AddHeader("Content-Type", "application/json-patch+json");

                    request.AddParameter("undefined", "[{\"op\": \"add\",\"path\": \"/relations/-\",\"value\": {\"rel\": \"System.LinkTypes.Hierarchy-Reverse\",\"url\": \"https://be4it.visualstudio.com/_apis/wit/workItems/" + vstsparent + "\",\"attributes\": {\"comment\": \"\"}}}]", ParameterType.RequestBody);
                    IRestResponse response = client.Execute(request);

                    if (response.StatusCode != System.Net.HttpStatusCode.OK && !response.Content.Contains("Relation already exists"))
                        throw new Exception(response.Content);
                }
                else
                    throw new Exception("wait what ?");
            }

            // relations
            var withrelations = issues.SelectMany(x => x.Relations).Select(x => new { from = x.IssueId, to = x.IssueToId });
            foreach (var relation in withrelations)
            {
                var vstsfrom = correlationmap[relation.from.ToString()];
                var vststo = correlationmap[relation.to.ToString()];
                string message = "migrated from redmine";
                if (vstsfrom != null && vststo != null)
                {
                    var client = new RestClient($"{redminemigration.Properties.Settings.Default.vsts_uri}/_apis/wit/workitems/{vstsfrom}?api-version=4.1");
                    var request = new RestRequest(Method.PATCH);
                    request.AddHeader("Authorization", redminemigration.Properties.Settings.Default.vsts_authorizationheader);
                    request.AddHeader("Content-Type", "application/json-patch+json");

                    request.AddParameter("undefined", "[{\"op\": \"add\",\"path\": \"/relations/-\",\"value\": {\"rel\": \"System.LinkTypes.Related\",\"url\": \"https://be4it.visualstudio.com/_apis/wit/workItems/" + vststo + "\",\"attributes\": {\"comment\": \"" + message + "\"}}}]", ParameterType.RequestBody);
                    IRestResponse response = client.Execute(request);

                    if (response.StatusCode != System.Net.HttpStatusCode.OK && !response.Content.Contains("Relation already exists"))
                        throw new Exception(response.Content);
                }
                else if (vstsfrom != null ^ vststo != null)
                {
                    var vstsissue = vstsfrom ?? vststo;
                    var nonvstsissue = redminemigration.Properties.Settings.Default.redmine_uri + "/issues/" + (vstsfrom == vstsissue ? relation.to : relation.from);

                    var client = new RestClient($"{redminemigration.Properties.Settings.Default.vsts_uri}/_apis/wit/workitems/{vstsissue}?api-version=4.1");
                    var request = new RestRequest(Method.PATCH);
                    request.AddHeader("Authorization", redminemigration.Properties.Settings.Default.vsts_authorizationheader);
                    request.AddHeader("Content-Type", "application/json-patch+json");

                    request.AddParameter("undefined", "[{\"op\": \"add\",\"path\": \"/relations/-\",\"value\": {\"rel\": \"Hyperlink\",\"url\": \"" + nonvstsissue + "\",\"attributes\": {\"comment\": \"" + message + "\"}}}]", ParameterType.RequestBody);
                    IRestResponse response = client.Execute(request);

                    if (response.StatusCode != System.Net.HttpStatusCode.OK && !response.Content.Contains("Relation already exists"))
                        throw new Exception(response.Content);
                }

                Trace.TraceInformation($"added link between {relation.from} and {relation.to}");
            }
        }


    }
}
