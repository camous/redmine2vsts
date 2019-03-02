# redmine2vsts
proof of concept migration tool for tranferring issue from redmine to visual studio team service (VSTS) using acmemapper

## settings

| property | type |  |
|---------------------|:------:|------------------------------------------------------------------------------------------------------------------------------|
| redmine_uri | string | redmine instance endpoint |
| redmine_internaluri | string | internal redmine endpoint (behind proxy, required to match attachments reference and be replaced by actual redmine endpoint) |
| redmine_apikey | string | redmine API key |
| redmine_projectid | integer | redmine project ID to migrate |
| vsts_uri | string | vsts/devops endpoint (https://XXXXX.visualstudio.com) |
| vsts_authorizationheader | string | Basic XXXXXX (could be a PAT/personal access token)
| vsts_projectname | string | vsts/devops project name

## mapping

acmemapper mapping file is located [here](redminemigration/Maps/issue.json)

beside regular redmine fields, you might customize following mapping

### Tracker / Work Item Type

Depending on custom tracked you had created in redmine, you could map them to specific type in VSTS. `Feature` and `Bug` mapping could have been omitted as no `$default` defined but kept for clarity.

```json
    {
      "redmine": "Tracker",
      "vsts": {
        "property": "Type",
        "fromsubproperty": "Name",
        "map": {
          "Proposal": "Task",
          "Support": "Task",
          "Decision": "Task",
          "Feature" : "Feature",
          "Bug" : "Bug"
        }
      }
    }
```

### Priority

VSTS has less granularity for priority, you can make your choice.

```json
    {
      "redmine": "Priority",
      "vsts": {
        "property": "/fields/Microsoft.VSTS.Common.Priority",
        "fromsubproperty": "Name",
        "map": {
          "Low": 4,
          "Normal": 3,
          "High": 3,
          "Urgent": 2,
          "Immediate": 1
        }
      }
    }
```

### Status / State

```json
    {
      "redmine": "Status",
      "vsts": {
        "property": "/fields/System.State",
        "fromsubproperty": "Name",
        "map": {
          "In Progress": "Active",
          "Feedback": "Active",
          "Approved": "Active",
          "Tested": "Active",
          "Rejected": "Closed"
        }
      }
    }
```

### Version / Iteration

Iterations have to exist in VSTS prior to migration. Version can be merged or even ignored: `ignoreIfNull` modifier is run after `map` operator. 

`patternValue` only allows the same level of within VSTS. eg final `IterationPath` would be `Acme\ACME Core`

```json
    {
      "redmine": "FixedVersion",
      "vsts": {
        "property": "/fields/System.IterationPath",
        "fromsubproperty": "Name",
        "map": {
          "Acme design & architecture": "ACME Core",
          "ACME 4.0 Anvils / Solution BlueSales": "ACME 4.0 Anvils BlueSales",
          "ACME 10.0 CMDB Bird Seed": "ACME 10.0 Bird Seed CMDB",
          "BOSS Interface": "ACME 4.0 Anvils BlueSales",
          "Blue Sales": "ACME 4.0 Anvils BlueSales",
          "ACME Technical Debt": null
        },
        "ignoreIfNull": true,
        "patternValue": "Acme\\{value}"
      }
    }
```

### Category / AreaPath

Category have to exist in VSTS prior to migration. Below example, `$default` catches all non-mapped category and redirect to `unknown` AreaPath in VSTS (which exists).

```json
    {
      "redmine": "Category",
      "vsts": {
        "property": "/fields/System.AreaPath",
        "fromsubproperty": "Name",
        "ignoreIfNull": true,
        "map": {
          "$default": "unknown",
          "PMO": "org-pmo",
          "VSTS": "infra-vsts",
          "acme-aspirin": "tool-aspirin",
          "acme-beans": "client-beans",
          "acme-cecil": "client-cecil"
        },
        "patternValue": "Acme\\{value}"
      }
    }
    ```