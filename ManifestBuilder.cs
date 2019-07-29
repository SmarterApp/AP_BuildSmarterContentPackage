using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Compression;

namespace BuildSmarterContentPackage
{
    public class ManifestBuilder
    {
        public string Content { get; set; }

        public List<Item> Items = new List<Item>();
        public List<Item> Stims = new List<Item>();

        // loop through the Items and Stimulus folders in the zip archive to generate lists of items and stims. From these lists, build the manifest content
        public ManifestBuilder()
        {            
        }

        public void BuildContent()
        {
            //loop through the Items and Stims lists and build the XML elements
            string output = "<manifest identifier=\"MANIFEST-QTI-1\" xmlns=\"http://www.imsglobal.org/xsd/apip/apipv1p0/imscp_v1p1\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                            "xsi:schemaLocation=\"http://www.imsglobal.org/xsd/apip/apipv1p0/imscp_v1p1 http://www.imsglobal.org/profile/apip/apipv1p0/apipv1p0_imscpv1p2_v1p0.xsd\">" +
                            "<metadata><schema>APIP Test</schema><schemaversion>1.0.0</schemaversion><lom xmlns=\"http://ltsc.ieee.org/xsd/apipv1p0/LOM/manifest\"></lom>" +
                            "</metadata><organizations/><resources>";

            foreach (Item currentItem in Items)
            {
                output += "<resource identifier=\"" + currentItem.Identifier + "\" type=\"" + currentItem.ResourceTypeVersion() + "\">";
                output += "<file href=\"" + currentItem.Href + "\"/>";

                if (currentItem.DependentAssets.Count > 0)
                {
                    if (currentItem.DependentAssets.Count > 0)
                    {
                        foreach (Item currentDependency in currentItem.DependentAssets)
                        {
                            output += "<dependency identifierref=\"" + currentDependency.Identifier + "\"/>";
                        }
                    }
                }

                if (currentItem.DependentWit != null)
                {
                    output += "<dependency identifierref=\"" + currentItem.DependentWit.Identifier + "\"/>";
                }
                if (currentItem.DependentTut != null)
                {
                    output += "<dependency identifierref=\"" + currentItem.DependentTut.Identifier + "\"/>";
                }
                if (currentItem.DependentStim != null)
                {
                    output += "<dependency identifierref=\"" + currentItem.DependentStim.Identifier + "\"/>";
                }
                output += "<dependency identifierref=\"" + currentItem.DependentMetadata.Identifier + "\"/></resource>";

                output += "<resource identifier=\"" + currentItem.DependentMetadata.Identifier + "\" type=\"" + currentItem.DependentMetadata.ResourceTypeVersion() + "\">";
                output += "<file href=\"" + currentItem.DependentMetadata.Href + "\"/></resource>";

                if (currentItem.DependentAssets.Count > 0) { 
                    foreach(Item currentDependency in currentItem.DependentAssets)
                    {
                        output += "<resource identifier=\"" + currentDependency.Identifier + "\" type=\"" + currentDependency.ResourceTypeVersion() + "\">";
                        output += "<file href=\"" + currentDependency.Href + "\"/></resource>";
                    }
                }
            }

            foreach(Item currentStim in Stims)
            {
                output += "<resource identifier=\"" + currentStim.Identifier + "\" type=\"" + currentStim.ResourceTypeVersion() + "\">";
                output += "<file href=\"" + currentStim.Href + "\"/>";

                if (currentStim.DependentWit != null)
                {
                    output += "<dependency identifierref=\"" + currentStim.DependentWit.Identifier + "\"/>";
                }
                if (currentStim.DependentAssets.Count > 0)
                {
                    foreach (Item currentDependency in currentStim.DependentAssets)
                    {
                        output += "<dependency identifierref=\"" + currentDependency.Identifier + "\"/>";
                    }
                }
                output += "<dependency identifierref=\"" + currentStim.DependentMetadata.Identifier + "\"/></resource>";

                output += "<resource identifier=\"" + currentStim.DependentMetadata.Identifier + "\" type=\"" + currentStim.DependentMetadata.ResourceTypeVersion() + "\">";
                output += "<file href=\"" + currentStim.DependentMetadata.Href + "\"/></resource>";

                if (currentStim.DependentAssets.Count > 0)
                {
                    foreach (Item currentDependency in currentStim.DependentAssets)
                    {
                        output += "<resource identifier=\"" + currentDependency.Identifier + "\" type=\"" + currentDependency.ResourceTypeVersion() + "\">";
                        output += "<file href=\"" + currentDependency.Href + "\"/></resource>";
                    }
                }
            }

            output += "</resources></manifest>";

            Content = output;
        }

    }
}
