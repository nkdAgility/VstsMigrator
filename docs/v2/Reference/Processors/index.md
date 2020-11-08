## Processors
**_This documentation is for a preview version of the Azure DevOps Migration Tools._**

[Overview](.././index.md) > [Reference](../index.md) > *Processors*

We provide a number of Processors that can be used to migrate diferent sorts of data.

Processor | Data Type | Description
----------|-----------|------------
[WorkItemTrackingProcessor](./WorkItemTrackingProcessor.md) | Work Items | Migrated any number of work items, their revisions, links, & attcahments

### Processor Options

 All processors have a minimum set of options that are required to run. 

#### Minimum Options to run
The `Enabled` options is common to all processors.


```JSON
    {
      "ObjectType": "ProcessorOptions",
      "Enabled": true,
    }
```