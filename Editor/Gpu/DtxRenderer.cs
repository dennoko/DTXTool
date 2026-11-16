using System;
using System.Collections.Generic;
using Dennoko.DTXTool.Core;
using UnityEditor;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// プレビューの更新（Docs/Impl/ARCHITECTURE.md §6.5）。WorkSession が所有する。
    /// 変更通知を受けた時点では何もせず、EditorApplication.update（WorkSession.Tick）で入力のキーを比べ、変わっていた時だけ合成し直す。
    /// レイヤーのマスクは内容のキーごとにキャッシュする（スライダー操作のたびに作り直さない）。
    /// </summary>
    internal sealed class DtxRenderer : IDisposable, ICompositeSource
    {
        /// <summary>参照しているテクスチャが再インポートされた時などに +1（次の Tick で作り直す）。</summary>
        public static int ExternalRevision;

        private sealed class MaskEntry
        {
            public ulong Key;
            public RenderTexture Texture;
        }

        private sealed class ArrayHash
        {
            public int[] Bits;
            public byte[] Paint;
            public byte[] Embedded;
            public ulong Hash;
        }

        private readonly WorkSession _s;
        private readonly GpuResources _gpu;
        private readonly Dictionary<string, MaskEntry> _masks = new Dictionary<string, MaskEntry>();
        private readonly Dictionary<string, ArrayHash> _arrayHashes = new Dictionary<string, ArrayHash>();
        private RenderTexture _previewBase, _previewNormal;
        private RenderTexture _liveMask;
        private (int, int, bool, float, int) _liveKey;
        private (int, int, int, string, int, int, int, int) _renderedKey = (-1, 0, 0, null, 0, 0, 0, 0);
        private bool _failed;

        public Compositor Compositor { get; }
        public LayerMaskBuilder Masks { get; }

        /// <summary>マテリアルへ注入する BaseMap（sRGB）。</summary>
        public RenderTexture PreviewBase => _previewBase;
        /// <summary>マテリアルへ注入する NormalMap（RGB、アルファ 1）。</summary>
        public RenderTexture PreviewNormal => _previewNormal;
        /// <summary>合成し直すたびに +1（UV ビューの再描画判定）。</summary>
        public int Version { get; private set; }

        /// <summary>プレビュー用の RenderTexture を作り直した（解像度の変更）。注入し直す合図。</summary>
        public event Action TargetsChanged;

        public DtxRenderer(WorkSession session, GpuResources gpu, TriMesh tri, bool needsWrap)
        {
            _s = session;
            _gpu = gpu;
            Compositor = new Compositor(gpu);
            Masks = new LayerMaskBuilder(gpu, tri, needsWrap, () => session.Active, session.RestoreFlags);
        }

        /// <summary>プレビューの解像度（出力解像度を上限に、設定値まで）。</summary>
        public static int PreviewSize
        {
            get
            {
                int max = Mathf.Clamp(SettingsService.Get(SettingKeys.PreviewSize), 256, 4096);
                return Mathf.Clamp(Mathf.Min(DocumentHost.instance.Doc.width, max), 16, 4096);
            }
        }

        /// <summary>EditorApplication.update から。入力が変わっていたら合成し直す。</summary>
        public void Tick()
        {
            int size = PreviewSize;
            bool live = DtxModeController.Mode == DtxMode.Reselect;
            if (live) _s.EnsureGpu();   // 作業中の選択のマスクを最新にする
            var key = (DocumentHost.instance.Revision, size, (int)DtxModeController.Mode, DtxModeController.EditingLayerId,
                live ? _s.Pipeline.Version : 0, ExternalRevision, _s.Tri.ActiveVersion, _s.EffectiveMargin(new Vector2Int(size, size)));
            bool lost = _previewBase == null || !_previewBase.IsCreated() || !_previewNormal.IsCreated();
            if (!lost && key.Equals(_renderedKey)) return;
            if (_failed && key.Equals(_renderedKey)) return;
            _renderedKey = key;
            try
            {
                Render(size);
                _failed = false;
            }
            catch (Exception e)
            {
                // 同じ入力で毎フレーム失敗し続けないよう、入力が変わるまで再試行しない
                _failed = true;
                Debug.LogException(e);
            }
        }

        /// <summary>入力が変わっていなくても合成し直す（作業開始直後）。</summary>
        public void RenderNow()
        {
            _renderedKey = (-1, 0, 0, null, 0, 0, 0, 0);
            Tick();
        }

        private void Render(int size)
        {
            EnsureTargets(size);
            var doc = DocumentHost.instance.Doc;
            Prune(doc);
            Compositor.Render(doc, size, this);
            Compositor.Present(_previewBase, _previewNormal);
            Version++;
            SceneView.RepaintAll();
        }

        private void EnsureTargets(int size)
        {
            bool lost = _previewBase == null || !_previewBase.IsCreated() || !_previewNormal.IsCreated();
            if (!lost && _previewBase.width == size) return;
            _gpu.Release(_previewBase);
            _gpu.Release(_previewNormal);
            _previewBase = _gpu.CreateRT("PreviewBase", size, size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB,
                FilterMode.Trilinear, TextureWrapMode.Repeat, true);
            _previewNormal = _gpu.CreateRT("PreviewNormal", size, size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear,
                FilterMode.Trilinear, TextureWrapMode.Repeat, true);
            ClearMasks();
            TargetsChanged?.Invoke();
        }

        // ── ICompositeSource ──

        public Texture2D Resolve(TextureRef reference) => TextureInfo.Resolve(reference);

        public Texture MaskFor(Layer layer, int size)
        {
            if (DtxModeController.Mode == DtxMode.Reselect && layer.id == DtxModeController.EditingLayerId)
            {
                var live = LiveMask(layer, size);
                if (live != null) return live;
            }

            int margin = _s.EffectiveMargin(new Vector2Int(size, size));
            ulong key = MaskKey(layer, size, margin);
            if (_masks.TryGetValue(layer.id, out var entry) && entry.Key == key && entry.Texture != null && entry.Texture.IsCreated())
                return entry.Texture;
            if (entry != null) _gpu.Release(entry.Texture);
            entry = new MaskEntry { Key = key, Texture = Masks.Build(layer, size, margin) };
            _masks[layer.id] = entry;
            return entry.Texture;
        }

        public void DoneWithMask(Layer layer, Texture mask)
        {
            // プレビューではキャッシュに残す
        }

        /// <summary>範囲の再調整中のレイヤーは、作業中の選択（EditState）のマスクをそのまま仕上げて使う。</summary>
        private Texture LiveMask(Layer layer, int size)
        {
            var raw = _s.Pipeline?.Mask;
            if (raw == null || raw.width != size) return null;
            var m = layer.mask;
            int feather = DocumentOps.FeatherPixels(m, size);
            var key = (_s.Pipeline.Version, feather, m.invert, m.opacity, size);
            if (_liveMask != null && _liveMask.IsCreated() && key.Equals(_liveKey)) return _liveMask;
            _gpu.Release(_liveMask);
            _liveMask = Masks.Finish(raw, size, feather, m.invert, m.opacity);
            _liveKey = key;
            return _liveMask;
        }

        private ulong MaskKey(Layer layer, int size, int margin)
        {
            var m = layer.mask;
            var h = new Fnv1a();
            h.Add((int)m.source);
            h.Add(size);
            h.Add(margin);
            h.Add(_s.Tri.ActiveVersion);
            h.Add(m.invert ? 1 : 0);
            h.Add(BitConverter.ToInt32(BitConverter.GetBytes(m.opacity), 0));
            h.Add(BitConverter.ToInt32(BitConverter.GetBytes(m.feather), 0));
            h.Add(ExternalRevision);
            ulong arrays = ArraysHash(layer.id, m);
            h.Add((int)arrays);
            h.Add((int)(arrays >> 32));
            foreach (char c in m.image.guid ?? string.Empty) h.Add((byte)c);
            foreach (char c in m.image.path ?? string.Empty) h.Add((byte)c);
            h.Add(m.embeddedSize);
            return h.Value;
        }

        /// <summary>大きな配列のハッシュは、配列そのものが入れ替わった時だけ計算し直す。</summary>
        private ulong ArraysHash(string layerId, MaskSpec m)
        {
            if (_arrayHashes.TryGetValue(layerId, out var c)
                && ReferenceEquals(c.Bits, m.selectedBits) && ReferenceEquals(c.Paint, m.paintRle) && ReferenceEquals(c.Embedded, m.embedded))
                return c.Hash;
            var h = new Fnv1a();
            h.Add(m.selectedBits);
            h.Add(m.paintRle);
            h.Add(m.embedded);
            _arrayHashes[layerId] = new ArrayHash { Bits = m.selectedBits, Paint = m.paintRle, Embedded = m.embedded, Hash = h.Value };
            return h.Value;
        }

        /// <summary>無くなったレイヤーのキャッシュを捨てる。</summary>
        private void Prune(DtxDocument doc)
        {
            if (_masks.Count == 0) return;
            List<string> dead = null;
            foreach (var id in _masks.Keys)
                if (doc.IndexOf(id) < 0) (dead ??= new List<string>()).Add(id);
            if (dead == null) return;
            foreach (var id in dead)
            {
                _gpu.Release(_masks[id].Texture);
                _masks.Remove(id);
                _arrayHashes.Remove(id);
            }
        }

        private void ClearMasks()
        {
            foreach (var e in _masks.Values) _gpu.Release(e.Texture);
            _masks.Clear();
            _gpu.Release(_liveMask);
            _liveMask = null;
        }

        public void Dispose()
        {
            ClearMasks();
            Compositor.Dispose();
            _gpu.Release(_previewBase);
            _gpu.Release(_previewNormal);
            _previewBase = _previewNormal = null;
        }
    }

    /// <summary>参照しているテクスチャが変わったら、プレビューを作り直す。</summary>
    internal sealed class DtxTextureChangeWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (SessionController.Current == null) return;
            if (imported.Length > 0 || deleted.Length > 0) DtxRenderer.ExternalRevision++;
        }
    }
}
