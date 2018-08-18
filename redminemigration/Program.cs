using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Redmine.Net.Api.Types;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace redminemigration
{
    class Program
    {
        static Redmine.Net.Api.RedmineManager redmine = new Redmine.Net.Api.RedmineManager("https://redmine.bearingpoint.com", "63b033218372d84c5d2a29665836f6dc27fd6db0");
        static Dictionary<string, Dictionary<int, object>> cache = new Dictionary<string, Dictionary<int, object>>();

        static void Main(string[] args)
        {
            var mapper = new acmemapper.Mapper("redmine", "vsts");

            var issue_id = "10877";//"10610"; 
            var issue = redmine.GetObject<Issue>(issue_id, new System.Collections.Specialized.NameValueCollection { { "include", "children,attachments,relations,changesets,journals,watchers"} });
            var mappedissue = mapper.Map<Issue, JObject>("issue", issue);

            var vstsissue = mappedissue.Values<JProperty>().Select(x => new VSTSField { op = "add", path = x.Name, value =x.Value.ToObject<dynamic>() }).ToList();

            // resolve dependencies
            // all value of type JObject with a type property
            var dependencies = vstsissue.Where(x => x.value is JObject);
            foreach (var dependency in dependencies)
                vstsissue.Single(x => x.path == dependency.path).value = ResolveDependency(dependency);

            vstsissue.Add(new VSTSField { op = "add", path = "/fields/System.Reason", value = "Work started" });

            var bodypayload = JsonConvert.SerializeObject(vstsissue);

            var client = new RestClient("https://be4it.visualstudio.com/Acme%20test/_apis/wit/workitems/$Task?bypassRules=True&api-version=4.1");
            var request = new RestRequest(Method.POST);
            request.AddHeader("Authorization", "Basic dnVwdDd1bnBsYWhieGxidmh1c3ZjZGh3ZnJnZmR3anNpeXd6cnNqbG9jY250Z2RyZm5yYTo=");
            request.AddHeader("Content-Type", "application/json-patch+json");
            request.AddParameter("undefined", bodypayload, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                throw new Exception(response.Content);
            Console.ReadKey();
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

                if (!cache.ContainsKey(dependencyType))
                    cache.Add(dependencyType, new Dictionary<int, object>());

                cache[dependencyType].Add(id,result);
            }

            return result;
        }
    }

    public class VSTSField
    {
        string from = null;
        public string op { get; set; }
        public string path { get; set; }
        public object value { get; set; }
    }
}
