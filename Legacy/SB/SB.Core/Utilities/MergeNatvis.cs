using System.Text;
using System.Xml;
using Serilog;

namespace SB
{
    // SEE: https://github.com/microsoft/vscode-cpptools/issues/10917#issuecomment-2189106550
    public static class MergeNatvis
    {
        public static string Merge(IEnumerable<string> filePaths)
        {
            const string nsUrl = "http://schemas.microsoft.com/vstudio/debugger/natvis/2010";

            // load files
            var docs = new List<XmlDocument>();
            foreach (var filePath in filePaths)
            {
                var doc = new XmlDocument();
                doc.Load(filePath);
                docs.Add(doc);
            }

            // create result
            var result = new XmlDocument();

            // setup xml declaration
            var declaration = result.CreateXmlDeclaration("1.0", "utf-8", null);
            result.AppendChild(declaration);

            // setup visualizer node
            var visualizer = result.CreateElement("AutoVisualizer", nsUrl);
            result.AppendChild(visualizer);

            // merge Type nodes
            var seenTypes = new HashSet<string>();
            foreach (var doc in docs)
            {
                var nsMgr = new XmlNamespaceManager(doc.NameTable);
                nsMgr.AddNamespace("vis", nsUrl);

                foreach (XmlNode typeNode in doc.SelectNodes("/vis:AutoVisualizer/vis:Type", nsMgr)!)
                {
                    string typeName = typeNode.Attributes!["Name"]!.Value;
                    string typePriority = typeNode.Attributes!["Priority"]?.Value ?? "0";
                    string typeKey = $"{typeName}_{typePriority}";

                    // check for duplicates
                    if (seenTypes.Contains(typeKey))
                    {
                        Log.Warning("Duplicate Type found: {typeName} with Priority {typePriority}. Skipping.", typeName, typePriority);
                        continue;
                    }
                    else
                    {
                        Log.Verbose("Adding Type: {typeName} with Priority {typePriority}.", typeName, typePriority);
                        seenTypes.Add(typeKey);
                        var importedNode = result.ImportNode(typeNode, true);
                        visualizer.AppendChild(importedNode);
                    }
                }
            }

            // dump content to string
            StringBuilder sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, new XmlWriterSettings
            {
                Indent = true,
                OmitXmlDeclaration = false
            }))
            {
                result.WriteTo(writer);
            }

            return sb.ToString();
        }
    };
}