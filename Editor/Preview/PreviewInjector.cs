using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dennoko.DTXTool
{
    /// <summary>対象マテリアルのどのプロパティへ注入するか（Docs/Impl/ARCHITECTURE.md §7.1）。</summary>
    internal static class ShaderPropertyResolver
    {
        private static readonly string[] BaseNames = { "_MainTex", "_BaseMap", "_BaseColorMap", "_BaseColorTexture" };
        private static readonly string[] NormalNames = { "_BumpMap", "_NormalMap", "_NormalTex" };

        /// <summary>2D テクスチャのプロパティ名（ドロップダウンの候補）。</summary>
        public static List<string> TextureProperties(Material m)
        {
            var list = new List<string>();
            if (m == null || m.shader == null) return list;
            var shader = m.shader;
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0
                    && Array.IndexOf(BaseNames, shader.GetPropertyName(i)) < 0 && Array.IndexOf(NormalNames, shader.GetPropertyName(i)) < 0) continue;
                list.Add(shader.GetPropertyName(i));
            }
            return list;
        }

        public static string AutoBase(Material m) => Auto(m, ShaderPropertyFlags.MainTexture, BaseNames);

        public static string AutoNormal(Material m) => Auto(m, ShaderPropertyFlags.Normal, NormalNames);

        /// <summary>設定で差し替えられていればそれを、無ければ自動判別したものを返す。使えるものが無ければ null。</summary>
        public static string ResolveBase(Material m) => Resolve(m, SettingsService.Get(SettingKeys.BaseProperty), AutoBase(m));

        public static string ResolveNormal(Material m) => Resolve(m, SettingsService.Get(SettingKeys.NormalProperty), AutoNormal(m));

        private static string Resolve(Material m, string chosen, string auto)
        {
            if (m == null) return null;
            if (!string.IsNullOrEmpty(chosen) && IsTexture2D(m, chosen)) return chosen;
            return auto;
        }

        private static string Auto(Material m, ShaderPropertyFlags flag, string[] names)
        {
            if (m == null || m.shader == null) return null;
            foreach (var name in names)
                if (IsTexture2D(m, name)) return name;
            var shader = m.shader;
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                if ((shader.GetPropertyFlags(i) & flag) != 0) return shader.GetPropertyName(i);
            }
            return null;
        }

        public static bool IsTexture2D(Material m, string name)
        {
            if (m == null || m.shader == null || string.IsNullOrEmpty(name)) return false;
            int i = m.shader.FindPropertyIndex(name);
            return i >= 0 && m.shader.GetPropertyType(i) == ShaderPropertyType.Texture
                   && m.shader.GetPropertyTextureDimension(i) == TextureDimension.Tex2D;
        }
    }

    /// <summary>
    /// プレビュー用 RenderTexture のマテリアルへの注入と復元（Docs/Impl/ARCHITECTURE.md §7.2）。
    /// 注入の前に必ず復旧ジャーナルを書き、元へ戻したら消す。Undo には記録しない。
    /// </summary>
    internal sealed class PreviewInjector
    {
        private const string LilUseBump = "_UseBumpMap";
        private const string NormalKeyword = "_NORMALMAP";

        private static readonly List<PreviewInjector> Live = new List<PreviewInjector>();

        private Material _material;
        private string _baseProp, _normalProp;
        private Texture _origBase, _origNormal;
        private bool _hasUseBump, _hasKeyword, _origKeyword;
        private float _origUseBump;
        private Func<Texture> _baseSource, _normalSource;

        public bool Active => _material != null;
        public bool Suspended { get; private set; }
        public Material Material => _material;
        public string BaseProperty => _baseProp;
        public string NormalProperty => _normalProp;
        /// <summary>注入前に入っていたテクスチャ（出力先フォルダや下地の既定に使う）。</summary>
        public Texture OriginalBase => _origBase;
        public Texture OriginalNormal => _origNormal;

        /// <summary>注入を始める。プロパティ名が null の側は何もしない。</summary>
        public void Begin(Material material, string baseProp, string normalProp, Func<Texture> baseSource, Func<Texture> normalSource)
        {
            End();
            if (material == null) return;
            _material = material;
            _baseProp = ShaderPropertyResolver.IsTexture2D(material, baseProp) ? baseProp : null;
            _normalProp = ShaderPropertyResolver.IsTexture2D(material, normalProp) ? normalProp : null;
            _baseSource = baseSource;
            _normalSource = normalSource;

            _origBase = _baseProp != null ? material.GetTexture(_baseProp) : null;
            _origNormal = _normalProp != null ? material.GetTexture(_normalProp) : null;
            _hasUseBump = _normalProp != null && material.HasProperty(LilUseBump);
            _origUseBump = _hasUseBump ? material.GetFloat(LilUseBump) : 0f;
            _hasKeyword = _normalProp != null && !_hasUseBump && HasKeyword(material, NormalKeyword);
            _origKeyword = _hasKeyword && material.IsKeywordEnabled(NormalKeyword);

            // 先に復旧ジャーナルを書く（この後で落ちても次回起動時に戻せる）
            PreviewRecoveryJournal.Write(Describe());
            Live.Add(this);
            Suspended = false;
            Apply();
        }

        /// <summary>RenderTexture をセットする（作り直された時にも呼ぶ）。</summary>
        public void Apply()
        {
            if (!Active || Suspended) return;
            if (_baseProp != null) _material.SetTexture(_baseProp, _baseSource?.Invoke());
            if (_normalProp != null)
            {
                _material.SetTexture(_normalProp, _normalSource?.Invoke());
                if (_hasUseBump) _material.SetFloat(LilUseBump, 1f);
                if (_hasKeyword) _material.EnableKeyword(NormalKeyword);
            }
        }

        /// <summary>一時的に元へ戻す（アセット保存・ベイクの間）。Resume で注入し直す。</summary>
        public void Suspend()
        {
            if (!Active || Suspended) return;
            RestoreOriginals();
            Suspended = true;
        }

        public void Resume()
        {
            if (!Active || !Suspended) return;
            Suspended = false;
            Apply();
        }

        /// <summary>元へ戻して終える。何度呼んでもよい。</summary>
        public void End()
        {
            if (!Active) return;
            RestoreOriginals();
            Live.Remove(this);
            _material = null;
            Suspended = false;
            if (Live.Count == 0) PreviewRecoveryJournal.Clear();
        }

        /// <summary>出力したテクスチャをマテリアルへ割り当てた時、「元の値」を差し替える（終了時にそれへ戻る）。</summary>
        public void SetOriginal(string property, Texture texture)
        {
            if (!Active) return;
            if (property == _baseProp) _origBase = texture;
            if (property == _normalProp)
            {
                _origNormal = texture;
                // ノーマルを割り当てたので、終了後も有効のままにする
                if (texture != null)
                {
                    if (_hasUseBump) _origUseBump = 1f;
                    if (_hasKeyword) _origKeyword = true;
                }
            }
            PreviewRecoveryJournal.Write(Describe());
        }

        private void RestoreOriginals()
        {
            if (_material == null) return;
            if (_baseProp != null) _material.SetTexture(_baseProp, _origBase);
            if (_normalProp != null)
            {
                _material.SetTexture(_normalProp, _origNormal);
                if (_hasUseBump) _material.SetFloat(LilUseBump, _origUseBump);
                if (_hasKeyword)
                {
                    if (_origKeyword) _material.EnableKeyword(NormalKeyword);
                    else _material.DisableKeyword(NormalKeyword);
                }
            }
        }

        private static bool HasKeyword(Material m, string keyword)
        {
            foreach (var k in m.shader.keywordSpace.keywordNames)
                if (k == keyword) return true;
            return false;
        }

        private PreviewRecoveryJournal.Entry Describe()
        {
            return new PreviewRecoveryJournal.Entry
            {
                material = IdOf(_material),
                baseProperty = _baseProp ?? string.Empty,
                baseTexture = IdOf(_origBase),
                normalProperty = _normalProp ?? string.Empty,
                normalTexture = IdOf(_origNormal),
                hasUseBump = _hasUseBump,
                useBump = _origUseBump,
                hasKeyword = _hasKeyword,
                keyword = _origKeyword,
            };
        }

        /// <summary>アセットだけを指す（シーン内のインスタンスは再起動後に辿れず、RenderTexture の参照も自然に消える）。</summary>
        private static string IdOf(UnityEngine.Object obj)
        {
            if (obj == null || !EditorUtility.IsPersistent(obj)) return string.Empty;
            return GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
        }

        // ── 全インスタンスへの操作（保存の直前・直後） ──

        public static bool AnyActive => Live.Count > 0;

        public static void SuspendAll()
        {
            foreach (var p in Live) p.Suspend();
        }

        public static void ResumeAll()
        {
            foreach (var p in Live) p.Resume();
        }
    }

    /// <summary>
    /// 保存の直前に元のテクスチャへ戻し、次の update で注入し直す（プレビュー用 RenderTexture をアセットやシーンに書かせない）。
    /// </summary>
    internal sealed class PreviewSaveGuard : UnityEditor.AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            PreviewGuards.SuspendForSave();
            return paths;
        }
    }

    [InitializeOnLoad]
    internal static class PreviewGuards
    {
        private static bool _resumePending;

        static PreviewGuards()
        {
            EditorSceneManager.sceneSaving += (scene, path) => SuspendForSave();
            EditorApplication.update += Update;
        }

        public static void SuspendForSave()
        {
            if (!PreviewInjector.AnyActive) return;
            PreviewInjector.SuspendAll();
            _resumePending = true;
        }

        private static void Update()
        {
            if (!_resumePending) return;
            _resumePending = false;
            PreviewInjector.ResumeAll();
            SceneView.RepaintAll();
        }
    }

    /// <summary>
    /// 復旧ジャーナル（UserSettings/DTXTool/preview_recovery.json）。正常に終われば消えるので、
    /// 起動時に残っていたら異常終了とみなし、マテリアルを元のテクスチャ参照へ戻す。
    /// </summary>
    internal static class PreviewRecoveryJournal
    {
        public const string FilePath = StoragePaths.Root + "/preview_recovery.json";

        [Serializable]
        public sealed class Entry
        {
            public string material = string.Empty;
            public string baseProperty = string.Empty;
            public string baseTexture = string.Empty;
            public string normalProperty = string.Empty;
            public string normalTexture = string.Empty;
            public bool hasUseBump;
            public float useBump;
            public bool hasKeyword;
            public bool keyword;
        }

        [Serializable]
        private sealed class Data
        {
            public List<Entry> entries = new List<Entry>();
        }

        public static void Write(Entry entry)
        {
            try
            {
                // アセットでないマテリアルは辿れないので保険の対象外
                if (string.IsNullOrEmpty(entry.material))
                {
                    Clear();
                    return;
                }
                var data = new Data();
                data.entries.Add(entry);
                AtomicFile.WriteAllText(FilePath, JsonUtility.ToJson(data));
            }
            catch (IOException e)
            {
                Debug.LogWarning("[DTXTool] " + e.Message);
            }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (IOException e)
            {
                Debug.LogWarning("[DTXTool] " + e.Message);
            }
        }

        public static bool Exists => File.Exists(FilePath);

        [InitializeOnLoadMethod]
        private static void RecoverOnLoad()
        {
            if (!Exists) return;
            // AssetDatabase が使える状態になってから。作業を自動再開した場合（注入中）は触らない
            EditorApplication.delayCall += () =>
            {
                if (PreviewInjector.AnyActive || EditorApplication.isPlayingOrWillChangePlaymode) return;
                Recover();
            };
        }

        /// <summary>ジャーナルの内容でマテリアルを元に戻す。戻した件数を返す。</summary>
        public static int Recover()
        {
            if (!Exists) return 0;
            Data data = null;
            try
            {
                data = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath));
            }
            catch (Exception)
            {
                // 壊れたジャーナルは捨てる
            }
            Clear();
            if (data?.entries == null) return 0;

            int restored = 0;
            foreach (var e in data.entries)
            {
                var material = Load<Material>(e.material);
                if (material == null) continue;
                bool changed = false;
                changed |= RestoreTexture(material, e.baseProperty, e.baseTexture);
                changed |= RestoreTexture(material, e.normalProperty, e.normalTexture);
                if (e.hasUseBump && material.HasProperty("_UseBumpMap") && !Mathf.Approximately(material.GetFloat("_UseBumpMap"), e.useBump))
                {
                    material.SetFloat("_UseBumpMap", e.useBump);
                    changed = true;
                }
                if (e.hasKeyword && material.IsKeywordEnabled("_NORMALMAP") != e.keyword)
                {
                    if (e.keyword) material.EnableKeyword("_NORMALMAP");
                    else material.DisableKeyword("_NORMALMAP");
                    changed = true;
                }
                if (!changed) continue;
                EditorUtility.SetDirty(material);
                restored++;
            }
            if (restored > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.LogWarning("[DTXTool] " + L10n.F("status.previewRecovered", restored));
            }
            return restored;
        }

        private static bool RestoreTexture(Material material, string property, string textureId)
        {
            if (string.IsNullOrEmpty(property) || !material.HasProperty(property)) return false;
            var texture = Load<Texture>(textureId);
            // 元がアセットでなかった（ID が空）なら、何が入っていたか分からないので触らない。ただし RenderTexture が残っていれば外す
            var current = material.GetTexture(property);
            if (string.IsNullOrEmpty(textureId))
            {
                if (current is RenderTexture)
                {
                    material.SetTexture(property, null);
                    return true;
                }
                return false;
            }
            if (current == texture) return false;
            material.SetTexture(property, texture);
            return true;
        }

        private static T Load<T>(string id) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(id) || !GlobalObjectId.TryParse(id, out var gid)) return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as T;
        }
    }
}
