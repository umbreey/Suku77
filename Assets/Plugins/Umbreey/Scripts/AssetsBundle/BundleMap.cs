using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public class BundleMap
    {
        public const string kSeparator = "#####\n";
        public Dictionary<string, LineData> Lines { get; private set; } = new Dictionary<string, LineData>(); // id -> line
        public ContentData Content { get; private set; } = new ContentData();
        public MetaData Meta => Content?.Meta;

        public BundleMap() { }
        
        public BundleMap(Dictionary<string, LineData> lines, ContentData content)
        {
            Lines = lines;
            Content = content;
        }
        
        public BundleMap(string bundlemap)
        {
            var split = bundlemap.Split(kSeparator);
            if (split.Length != 2)
            {
                Log.E("invalid bundlemap");
                return;
            }
            
            // lines
            Lines = new Dictionary<string, LineData>();
            var textLines = split[0].Split("\n");
            
            foreach(string t in textLines)
            {
                if (string.IsNullOrEmpty(t))
                    continue;
                
                LineData line = new LineData(t);
                if (Lines.TryGetValue(line.id, out var existingLine))
                {
                    Log.E($"bundlemap duplicate line id: {line.id}. existing: {existingLine.name} -> new: {line.name}");
                }
                
                Lines[line.id] = line;
            }

            // content
            Content = JsonUtility.FromJson<ContentData>(split[1]);
        }

        public LineData GetLineData(string id)
        {
            return Lines?.GetValueOrDefault(id);
        }

        public Reference GetReference(string id)
        {
            if (Lines.TryGetValue(id, out var line))
            {
                var abName = line.name;
                foreach (var reference in Content.References)
                {
                    if (reference.ABName == abName)
                    {
                        return reference;
                    }
                }
            }
            
            return null;
        }
        
        public class LineData
        {
            public string name => $"{id}_{md5}.{ext}";
            public string id { get; private set; }
            public string md5 { get; private set; }
            public string ext { get; private set; }
            public int size { get; private set; }
            public bool raw { get; private set; }
            
            public string text { get; private set; }
            
            public LineData(string text)
            {
                this.text = text;
                
                var splits = text.Split(":");
                id = splits[0];
                md5 = splits[1];
                ext = splits[2];
                size = int.Parse(splits[3]);
                raw = splits[4] == "1";
            }
            
            public override string ToString()
            {
                return text;
            }

            public bool Equals(LineData other)
            {
                if (other == null) return false;
                bool equal = (text == other.text);
                
                if (!equal)
                {
                    // 特殊情况处理，如果仅大小相差在256字节内，假定为元信息的轻微变更，不影响实际序列化内容
                    if (id == other.id && md5 == other.md5 && ext == other.ext && raw == other.raw)
                    {
                        if (System.Math.Abs(size - other.size) < 256)
                        {
                            return true;
                        }
                    }
                }
                
                return equal;
            }
        }

        [System.Serializable]
        public class MetaData
        {
            public int Build;
            public int UpdateBuild;
            public bool IsEncrypt;
            
            public string Branch;
            public string Version;
            public int Revision;

            public MetaData Clone()
            {
                return JsonUtility.FromJson<MetaData>(JsonUtility.ToJson(this));
            }
        }
        
        [System.Serializable]
        public class ContentData
        {
            public MetaData Meta = new MetaData();
            public List<Reference> References = new List<Reference>();
        }
        
        [System.Serializable]
        public class Reference
        {
            public string ABName;
            public string[] AssetNames;
            public string[] DependABNames;
        }
    }
}