using System.Text;

namespace Luban
{
    public abstract class EditorBeanBase
    {
        public abstract void LoadJson(global::SimpleJSON.JSONObject json);

        public abstract void SaveJson(global::SimpleJSON.JSONObject json);

        public void LoadJsonFile(string file)
        {
            string jsonText = System.IO.File.ReadAllText(file, Encoding.UTF8);
            LoadJson((global::SimpleJSON.JSONObject)global::SimpleJSON.JSON.Parse(jsonText));
        }

        public void SaveJsonFile(string file)
        {
            var json = new global::SimpleJSON.JSONObject();
            SaveJson(json);
            System.IO.File.WriteAllText(file, json.ToString(), Encoding.UTF8);
        }
    }
}
