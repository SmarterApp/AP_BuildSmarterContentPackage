using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BuildSmarterContentPackage
{
    public class Item
    {
        public enum ResourceType
        {
            Item,
            Stim,
            Metadata,
            AllOtherAssets
        }

        const string ItemResourceType = "imsqti_apipitem_xmlv2p2";
        const string StimResourceType = "imsqti_apipstimulus_xmlv2p2";
        const string MetadataResourceType = "resourcemetadata/apipv1p0";
        const string AllOtherAssestsResourceType = "associatedcontent/apip_xmlv1p0/learning-application-resource";

        public string Identifier { get; set; }
        public ResourceType Type { get; set; }
        public string Href { get; set; }
        public string Folder { get; set; }
        public bool IsADependency { get; set; }
        
        public List<Item> DependentAssets = new List<Item>();
        public Item DependentStim { get; set; }
        public Item DependentWit { get; set; }
        public Item DependentTut { get; set; }
        public Item DependentMetadata { get; set; }
        public string ResourceTypeVersion()
        {
            switch (Type)
            {
                case ResourceType.Item:
                    return ItemResourceType;
                case ResourceType.Stim:
                    return StimResourceType;
                case ResourceType.Metadata:
                    return MetadataResourceType;
                case ResourceType.AllOtherAssets:
                    return AllOtherAssestsResourceType;
                default:
                    return "UNKNOWN";
            }   
        }
    }
}
