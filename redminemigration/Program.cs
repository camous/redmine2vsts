using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Redmine.Net.Api.Types;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace redminemigration
{
    class Program
    {
        static Redmine.Net.Api.RedmineManager redmine = new Redmine.Net.Api.RedmineManager(Properties.Settings.Default.redmine_uri, Properties.Settings.Default.redmine_apikey);
        static Dictionary<string, Dictionary<int, object>> cache = new Dictionary<string, Dictionary<int, object>>();
        static JObject correlationmap = new JObject();
        static readonly string mapfilename = $"correlationmap_{Properties.Settings.Default.vsts_projectname}.json";
        static readonly string project_id = Properties.Settings.Default.redmine_projectid;

        static void Main(string[] args)
        {
            if (System.IO.File.Exists(mapfilename))
            {
                correlationmap = JObject.Parse(System.IO.File.ReadAllText(mapfilename));
                if (correlationmap["exceptions"] != null)
                    ((JObject)correlationmap["exceptions"]).ListChanged += Correlationmap_ListChanged;
            }

            correlationmap.ListChanged += Correlationmap_ListChanged;
            var mapper = new acmemapper.Mapper("redmine", "vsts");

            var issues = redmine.GetObjects<Issue>(new System.Collections.Specialized.NameValueCollection {
                { "project_id", project_id},
                { "status_id","*"},
                { "sort", "id" } });           

            foreach (var redmineissue in issues)
            {
                if(correlationmap.ContainsKey(redmineissue.Id.ToString()))
                {
                    Trace.TraceInformation($"#{redmineissue.Id} already migrated");
                    continue;
                }

                var issue_id = redmineissue.Id.ToString();
                var issue = redmine.GetObject<Issue>(issue_id, new System.Collections.Specialized.NameValueCollection {
                    { "include", "children,attachments,relations,changesets,journals,watchers" } });

                System.IO.File.WriteAllText($"./json/{redmineissue.Id}_redmine.json", JsonConvert.SerializeObject(issue, Formatting.Indented));

                var mappedissue = mapper.Map<Issue, JObject>("issue", issue);

                var workitemtype = mappedissue["Type"].Value<string>();
                mappedissue.Remove("Type");

                var vstsissue = mappedissue.Values<JProperty>().Select(x => new VSTSField {
                    op = "add",
                    path = x.Name,
                    value = x.Value.ToObject<dynamic>() }).ToList();

                // resolve dependencies
                // all value of type JObject with a type property
                var dependencies = vstsissue.Where(x => x.value is JObject);
                foreach (var dependency in dependencies)
                    vstsissue.Single(x => x.path == dependency.path).value = ResolveDependency(dependency);

                // rework description
                var description = vstsissue.Single(x => x.path == "/fields/System.Description");
                description.value = ((string)description.value).Replace("\n", "<br />");
                description.value += $"<br /><br />--<br /> migrated from redmine {Properties.Settings.Default.redmine_uri}/issues/{issue.Id}";
                description.value = ConvertUrlsToLinks(description.value);

                // create history
                string history = String.Empty;
                foreach (var journal in issue.Journals)
                {
                    var details = String.Empty;
                    foreach (var detail in journal.Details)
                        details += $"<li>{detail.Name} : from \"{detail.OldValue ?? "undefined"}\" to \"{detail.NewValue ?? "undefined"}\"</li>";

                    string v = (!String.IsNullOrEmpty(details) ? "<ul>" + details + "</ul>" : "");
                    history += $@"<small>{journal.User.Name} on {journal.CreatedOn}</small><br />" + v + ConvertUrlsToLinks(journal.Notes)?.Replace("\n","<br />") + "<br /><hr />";
                }

                // attachments
                foreach (var attachment in issue.Attachments)
                {
                    var redmineattachmenturl = attachment.ContentUrl.Replace(Properties.Settings.Default.redmine_internaluri, Properties.Settings.Default.redmine_uri);
                    Trace.TraceInformation($"downloading {redmineattachmenturl}");
                    var body = redmine.DownloadFile(redmineattachmenturl);

                    System.IO.File.WriteAllBytes($"./attachments/{redmineissue.Id}_{attachment.FileName}", body);

                    var vstsurl = UploadVSTSAttachment(body, attachment.FileName, attachment.ContentType);
                    vstsissue.Add(new VSTSField
                    {
                        op = "add",
                        path = "/relations/-",
                        value = new
                        {
                            rel = "AttachedFile",
                            url = vstsurl,
                            attributes = new { comment = attachment.Description }
                        }
                    });

                    // do we have reference of this screenshot in description or history ?
                    description.value = ((string)description.value).Replace($"!{attachment.FileName}!", $"<img src=\"{vstsurl}\" />");
                    history = history.Replace($"!{attachment.FileName}!", $"<img src=\"{vstsurl}\" />");
                }

                if (!String.IsNullOrEmpty(history))
                    vstsissue.Add(new VSTSField { op = "add", path = "/fields/System.History", value = history });

                var bodypayload = JsonConvert.SerializeObject(vstsissue, Formatting.Indented);
                System.IO.File.WriteAllText($"./json/{redmineissue.Id}_vstspayload.json", bodypayload);

                var client = new RestClient($"{Properties.Settings.Default.vsts_uri}/{Properties.Settings.Default.vsts_projectname}/_apis/wit/workitems/${workitemtype}?bypassRules=True&api-version=4.1");
                var request = new RestRequest(Method.POST);
                request.AddHeader("Authorization", Properties.Settings.Default.vsts_authorizationheader);
                request.AddHeader("Content-Type", "application/json-patch+json");
                request.AddParameter("undefined", bodypayload, ParameterType.RequestBody);
                IRestResponse response = client.Execute(request);

                System.IO.File.WriteAllText($"./json/{redmineissue.Id}_vstsresponse.json", JsonConvert.SerializeObject(response.Content, Formatting.Indented));

                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    var jsonresponse = JObject.Parse(response.Content);
                    correlationmap.Add(issue.Id.ToString(), jsonresponse["id"]); // we save the correlation between redmine & vsts for later usage (relation)
                    Trace.TraceInformation($"#{issue.Id} {issue.Subject} migrated");
                }
                else
                {
                    if (correlationmap["exceptions"] == null)
                    {
                        correlationmap["exceptions"] = new JObject();
                        ((JObject)correlationmap["exceptions"]).ListChanged += Correlationmap_ListChanged;
                    }

                    Trace.TraceWarning($"#{issue.Id} {issue.Subject} error");

                    ((JObject)correlationmap["exceptions"]).Add(issue.Id.ToString(), response.Content);
                }
                
            }
        }

        private static void Correlationmap_ListChanged(object sender, System.ComponentModel.ListChangedEventArgs e)
        {
            System.IO.File.WriteAllText(mapfilename, JsonConvert.SerializeObject(correlationmap, Formatting.Indented));
        }

        static object ResolveDependency(dynamic field)
        {
            object result = null;
            string dependencyType = field.value.type.ToString();
            int id = field.value.id;

            if(cache.ContainsKey(dependencyType) &&  cache[dependencyType].ContainsKey(id))
                result = cache[dependencyType][id];
            else
            {
                // ugly !
                // get current Redmine.Api assembly namefor User, later we will replace User by the type we are looking for ...
                var assemblyname = typeof(User).AssemblyQualifiedName;
                var type = Type.GetType(assemblyname.Replace("User", dependencyType));

                try
                {
                    var remoteresult = typeof(Redmine.Net.Api.RedmineManager)
                        .GetMethod("GetObject").
                        MakeGenericMethod(type).
                        Invoke(redmine, new object[] { id.ToString(), null });

                    switch (remoteresult)
                    {
                        case User i:
                            result = i.Email;
                            break;
                        default:
                            throw new Exception($"{dependencyType} type not supported for dependency");
                    }

                } catch(System.Reflection.TargetInvocationException ex)
                {
                    result = String.Empty;
                }

                if (!cache.ContainsKey(dependencyType))
                    cache.Add(dependencyType, new Dictionary<int, object>());

                cache[dependencyType].Add(id,result);
            }

            return result;
        }

        static string UploadVSTSAttachment(byte[] content, string filename, string contentType)
        {
            var client = new RestClient($"{Properties.Settings.Default.vsts_uri}/{Properties.Settings.Default.vsts_projectname}/_apis/wit/attachments?fileName={filename}&api-version=4.1");
            var request = new RestRequest(Method.POST);
            request.AddHeader("Authorization", Properties.Settings.Default.vsts_authorizationheader);
            request.AddHeader("Content-Type", "application/octet-stream");
            request.AddParameter("undefined", content, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);

            if (response.StatusCode != System.Net.HttpStatusCode.OK && response.StatusCode != System.Net.HttpStatusCode.Created)
                throw new Exception($"error while downloading {filename} {response.StatusCode}");

            return JObject.Parse(response.Content)["url"].Value<string>();
        }

        static string ConvertUrlsToLinks(string msg)
        {
            if (msg == null)
                return null;

            string regex = @"((www\.|(http|https|ftp|news|file)+\:\/\/)[&#95;.a-z0-9-]+\.[a-z0-9\/&#95;:@=.+?,##%&~_-]*[^.|\'|\# |!|\(|?|,| |>|<|;|\)])";
            Regex r = new Regex(regex, RegexOptions.IgnoreCase);
            return r.Replace(msg, "<a href=\"$1\">$1</a>");
        }
    }

    [DebuggerDisplay("{path} : {value}")]
    public class VSTSField
    {
        string from = null;
        public string op { get; set; }
        public string path { get; set; }
        public dynamic value { get; set; }
    }
}
