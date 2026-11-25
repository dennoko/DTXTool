using System.IO;
using System.Text;
using UnityEditor;

namespace Dennoko.DTXTool
{
    /// <summary>出力先フォルダとファイル名（Docs/Impl/ARCHITECTURE.md §8）。</summary>
    internal static class OutputPaths
    {
        public const string BaseSuffix = "_BaseMap";
        public const string NormalSuffix = "_NormalMap";

        /// <summary>保存先フォルダを決めて、無ければ作る。</summary>
        public static string ResolveFolder(WorkSession s)
        {
            var dir = FolderFor(s);
            EnsureAssetFolder(dir);
            return dir;
        }

        /// <summary>保存先フォルダ（作らない。表示用）。元のテクスチャがあればそれと同じフォルダ。</summary>
        public static string FolderFor(WorkSession s)
        {
            if (SettingsService.Get(SettingKeys.UseTextureFolder) && s != null)
            {
                var tex = s.OriginalBaseTexture;
                var path = tex != null ? AssetDatabase.GetAssetPath(tex) : null;
                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/"))
                    return Path.GetDirectoryName(path).Replace('\\', '/');
            }
            var dir = SettingsService.Get(SettingKeys.OutputDir);
            if (string.IsNullOrWhiteSpace(dir) || !dir.Replace('\\', '/').StartsWith("Assets")) dir = SettingKeys.OutputDir.Default;
            return dir.Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>ファイル名の基準（設定が空なら対象の名前）。</summary>
        public static string BaseName(WorkSession s)
        {
            var n = SettingsService.Get(SettingKeys.OutputName);
            if (!string.IsNullOrWhiteSpace(n)) return Sanitize(n.Trim());
            return Sanitize(s != null && s.Target != null ? s.Target.name : "DTXTool");
        }

        public static string BasePng(string folder, string name) => folder + "/" + name + BaseSuffix + ".png";

        public static string NormalPng(string folder, string name) => folder + "/" + name + NormalSuffix + ".png";

        /// <summary>
        /// 実際に書く BaseMap / NormalMap のパス。
        /// overwrite = true: 同じ名前のファイルがあっても上書きする（ドキュメントが読んでいるテクスチャだけは避ける）。
        /// overwrite = false: どちらかが既にあれば、両方の末尾に「 1」「 2」… と同じ番号を付け、どちらも無い番号にする。
        /// </summary>
        public static (string basePath, string normalPath) PngPaths(string folder, string name, Dennoko.DTXTool.Core.DtxDocument doc, bool overwrite, bool exportBase = true, bool exportNormal = true)
        {
            string basePath = BasePng(folder, name);
            string normalPath = NormalPng(folder, name);
            if (overwrite) return (AvoidReferenced(basePath, doc), AvoidReferenced(normalPath, doc));

            var used = Referenced(doc);
            bool Free(string p) => !used.Contains(p) && !File.Exists(p) && !Directory.Exists(p);
            bool BaseOk(string p) => !exportBase || Free(p);
            bool NormalOk(string p) => !exportNormal || Free(p);
            if (BaseOk(basePath) && NormalOk(normalPath)) return (basePath, normalPath);
            for (int i = 1; ; i++)
            {
                string b = Numbered(basePath, i);
                string n = Numbered(normalPath, i);
                if (BaseOk(b) && NormalOk(n)) return (b, n);
            }
        }

        /// <summary>拡張子の前に「 番号」を付ける（a/b_BaseMap.png → a/b_BaseMap 1.png）。</summary>
        public static string Numbered(string path, int number)
        {
            string dir = Path.GetDirectoryName(path).Replace('\\', '/');
            return dir + "/" + Path.GetFileNameWithoutExtension(path) + " " + number + Path.GetExtension(path);
        }

        /// <summary>
        /// ドキュメントが参照しているテクスチャ（下地など）と同じパスには書かない。
        /// 自分の入力を上書きすると、出力するたびにレイヤーが重なっていくため、名前に _2, _3 … を足して避ける。
        /// </summary>
        public static string AvoidReferenced(string path, Dennoko.DTXTool.Core.DtxDocument doc)
        {
            var used = Referenced(doc);
            if (!used.Contains(path)) return path;
            string dir = Path.GetDirectoryName(path).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string candidate = dir + "/" + name + "_" + i + ext;
                if (!used.Contains(candidate)) return candidate;
            }
        }

        /// <summary>ドキュメントが読んでいるテクスチャのパス。</summary>
        private static System.Collections.Generic.HashSet<string> Referenced(Dennoko.DTXTool.Core.DtxDocument doc)
        {
            var used = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            void Add(Dennoko.DTXTool.Core.TextureRef r)
            {
                if (r == null || r.IsEmpty) return;
                string p = string.IsNullOrEmpty(r.guid) ? null : AssetDatabase.GUIDToAssetPath(r.guid);
                if (string.IsNullOrEmpty(p)) p = r.path;
                if (!string.IsNullOrEmpty(p)) used.Add(p);
            }
            doc.EnsureVariations();
            foreach (var v in doc.variations)
            {
                Add(v.background.baseTexture);
                Add(v.background.normalTexture);
                foreach (var l in v.layers)
                {
                    Add(l.baseTexture);
                    Add(l.normalTexture);
                    Add(l.normalMask);
                    Add(l.mask.image);
                }
            }
            return used;
        }

        public static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name) sb.Append(System.Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.Length == 0 ? "DTXTool" : sb.ToString();
        }

        public static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
