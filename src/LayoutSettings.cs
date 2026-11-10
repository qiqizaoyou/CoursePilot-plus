using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CoursePilot
{
    // Selection is separate from saved manual rectangles: restoring defaults never erases them.
    internal sealed class LayoutSettings
    {
        public string Device;
        public bool[] Custom = new bool[3];
        internal static LayoutSettings Load(string path)
        {
            try { var value = new JavaScriptSerializer().Deserialize<LayoutSettings>(File.ReadAllText(path)); if (value != null && value.Custom != null && value.Custom.Length == 3) return value; } catch { }
            return new LayoutSettings();
        }
        internal void Save(string path)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, new JavaScriptSerializer().Serialize(this), Encoding.UTF8); }
        internal Regions Active(Regions saved, CalibrationProfile profile)
        {
            Regions result = new Regions();
            for (int i = 0; i < 3; i++) if (Custom[i] && profile.IsConfirmed(saved, i)) CopyGroup(saved, result, i); else Custom[i] = false;
            return result;
        }
        internal void Defaults() { Custom = new bool[3]; }
        internal static void CopyGroup(Regions from, Regions to, int group)
        {
            if (group == 0) { to.List = from.List; to.Status = from.Status; }
            else if (group == 1) { to.Task = from.Task; to.Video = from.Video; to.Back = from.Back; }
            else { to.QuizHeader = from.QuizHeader; to.QuizBody = from.QuizBody; }
        }
        internal string Describe()
        { return String.Join(" · ", Enumerable.Range(0, 3).Select(i => CalibrationAreas.GroupName(i) + (Custom[i] ? "：已保存自定义" : "：默认布局"))); }
    }

    internal sealed class LayoutApproval
    {
        internal CalibrationEnvironment Environment;
        private string key;
        private static string Key(Regions regions) { return String.Join("|", Enumerable.Range(0, 3).Select(i => CalibrationAreas.Key(regions, i))); }
        internal void Clear() { Environment = null; key = null; }
        internal void Accept(Regions regions, CalibrationEnvironment environment) { Environment = environment; key = Key(regions); }
        internal bool Ready(Regions regions, CalibrationEnvironment environment)
        { return Environment != null && Environment.Matches(environment) && key == Key(regions); }
    }
}
