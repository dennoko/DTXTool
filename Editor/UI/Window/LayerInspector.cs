using System;
using System.Collections.Generic;
using Dennoko.DTXTool.Core;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Background = Dennoko.DTXTool.Core.Background;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// ドキュメント ⇄ UI 要素。表示は常にドキュメントから SetValueWithoutNotify で作り、入力は DocumentCommands に流すだけ
    /// （UI は値を持たない。Undo / Redo・PSD の読み込みでも同じ経路で表示が揃う）。
    /// </summary>
    internal static class DocBind
    {
        private static DocumentHost H => DocumentHost.instance;

        /// <summary>選択中のレイヤーの値。</summary>
        public static void Layer<T>(BaseField<T> field, string undoKey, Func<Layer, T> get, Action<Layer, T> set)
        {
            field.RegisterValueChangedCallback(e =>
            {
                // 複合フィールドの中の欄から上がってくる同じ型のイベントは無視する
                if (e.target != field) return;
                DocumentCommands.EditSelected(L10n.T(undoKey), l => set(l, e.newValue));
            });
            UiHub.Watch(field, () =>
            {
                var l = H.SelectedLayer;
                if (l != null) field.SetValueWithoutNotify(get(l));
            });
        }

        /// <summary>選択中のレイヤーのテクスチャ参照。</summary>
        public static void LayerTexture(ObjectField field, string undoKey, Func<Layer, TextureRef> get, Action<Layer, TextureRef> set)
        {
            field.objectType = typeof(Texture2D);
            field.RegisterValueChangedCallback(e =>
            {
                if (e.target != field) return;
                var reference = TextureInfo.ToRef(e.newValue as Texture);
                DocumentCommands.EditSelected(L10n.T(undoKey), l => set(l, reference));
            });
            UiHub.Watch(field, () =>
            {
                var l = H.SelectedLayer;
                if (l != null) field.SetValueWithoutNotify(TextureInfo.Resolve(get(l)));
            });
        }

        /// <summary>下地の値。</summary>
        public static void Background<T>(BaseField<T> field, string undoKey, Func<Background, T> get, Action<Background, T> set)
        {
            field.RegisterValueChangedCallback(e =>
            {
                if (e.target != field) return;
                DocumentCommands.EditBackground(L10n.T(undoKey), b => set(b, e.newValue));
            });
            UiHub.Watch(field, () => field.SetValueWithoutNotify(get(H.Doc.background)));
        }

        public static void BackgroundTexture(ObjectField field, string undoKey, Func<Background, TextureRef> get, Action<Background, TextureRef> set)
        {
            field.objectType = typeof(Texture2D);
            field.RegisterValueChangedCallback(e =>
            {
                if (e.target != field) return;
                var reference = TextureInfo.ToRef(e.newValue as Texture);
                DocumentCommands.EditBackground(L10n.T(undoKey), b => set(b, reference));
            });
            UiHub.Watch(field, () => field.SetValueWithoutNotify(TextureInfo.Resolve(get(H.Doc.background))));
        }

        /// <summary>複数のボタンから 1 つを選ぶ（選択中に .dennoko-button-active）。current は今の値、choose は選んだ時の処理。</summary>
        public static void Choice<T>(IReadOnlyList<(Button button, T value)> items, Func<T> current, Action<T> choose)
        {
            foreach (var (button, value) in items)
            {
                var v = value;
                button.clicked += () => choose(v);
            }
            if (items.Count == 0) return;
            UiHub.Watch(items[0].button, () =>
            {
                var c = current();
                foreach (var (button, value) in items) Bind.Active(button, EqualityComparer<T>.Default.Equals(value, c));
            });
        }
    }

    /// <summary>
    /// 選択中のレイヤー（または下地）の設定カード。
    ///   レイヤー設定: 名前・ブレンド・不透明度・マスク（範囲の再調整 / 画像・反転・不透明度・ぼかし）
    ///   質感        : ベース（単色 / テクスチャ + Tint）とノーマル（なし / 平坦化 / マップ + 強さ）
    ///   配置        : タイリング / デカール（テクスチャを使う時だけ出す）
    /// 使わない欄は隠す（グレーアウトにしない）。
    /// </summary>
    internal sealed class LayerInspector
    {
        private static readonly BlendMode[] BlendModes =
            { BlendMode.Normal, BlendMode.Multiply, BlendMode.Additive, BlendMode.Screen, BlendMode.Overlay };

        private readonly VisualElement _root;

        public LayerInspector(VisualElement root)
        {
            _root = root;
            BuildLayerCard(root);
            BuildMaterialCard(root);
            BuildPlacementCard(root);
            BuildBackgroundCard(root);
            UiHub.Watch(root.Q("layer-card"), RefreshVisibility);
        }

        public static string BlendLabel(BlendMode mode)
        {
            switch (mode)
            {
                case BlendMode.Multiply: return L10n.T("blend.multiply");
                case BlendMode.Additive: return L10n.T("blend.additive");
                case BlendMode.Screen: return L10n.T("blend.screen");
                case BlendMode.Overlay: return L10n.T("blend.overlay");
                default: return L10n.T("blend.normal");
            }
        }

        // ── レイヤー設定（基本とマスク） ──

        private void BuildLayerCard(VisualElement root)
        {
            root.Q<Label>("layer-header").text = L10n.T("layer.header");

            var name = root.Q<TextField>("layer-name");
            name.label = L10n.T("layer.name");
            name.isDelayed = true;
            DocBind.Layer(name, "undo.rename", l => l.name, (l, v) => l.name = string.IsNullOrWhiteSpace(v) ? l.name : v.Trim());

            var blend = root.Q<DropdownField>("layer-blend");
            blend.label = L10n.T("layer.blend");
            var labels = new List<string>();
            foreach (var m in BlendModes) labels.Add(BlendLabel(m));
            blend.choices = labels;
            blend.RegisterValueChangedCallback(e =>
            {
                if (e.target != blend || blend.index < 0) return;
                var mode = BlendModes[blend.index];
                DocumentCommands.EditSelected(L10n.T("undo.blend"), l => l.blendMode = mode);
            });
            UiHub.Watch(blend, () =>
            {
                var l = DocumentHost.instance.SelectedLayer;
                if (l != null) blend.SetValueWithoutNotify(BlendLabel(l.blendMode));
            });

            var opacity = root.Q<Slider>("layer-opacity");
            opacity.label = L10n.T("layer.opacity");
            DocBind.Layer(opacity, "undo.opacity", l => l.opacity, (l, v) => l.opacity = Mathf.Clamp01(v));

            root.Q<Label>("mask-title").text = L10n.T("mask.title");
            root.Q<Label>("mask-source-label").text = L10n.T("mask.source");
            var source = root.Q<Label>("mask-source");
            var reselect = root.Q<Button>("mask-reselect");
            reselect.clicked += () =>
            {
                var l = DocumentHost.instance.SelectedLayer;
                if (l == null) return;
                if (DtxModeController.Mode == DtxMode.Reselect && DtxModeController.EditingLayerId == l.id) DtxModeController.Confirm();
                else DtxModeController.BeginReselect(l.id);
            };
            UiHub.Watch(reselect, () =>
            {
                var l = DocumentHost.instance.SelectedLayer;
                if (l == null) return;
                bool editing = DtxModeController.Mode == DtxMode.Reselect && DtxModeController.EditingLayerId == l.id;
                source.text = editing ? L10n.T("layers.reselecting") : SourceLabel(l.mask.source);
                reselect.text = editing ? L10n.T("button.confirm")
                    : l.mask.source == MaskSource.Selection ? L10n.T("button.reselect") : L10n.T("button.toSelection");
                reselect.tooltip = editing ? string.Empty
                    : l.mask.source == MaskSource.Selection ? L10n.T("tooltip.reselect") : L10n.T("tooltip.toSelection");
                Bind.Active(reselect, editing);
            });

            var image = root.Q<ObjectField>("mask-image");
            image.label = L10n.T("mask.image");
            image.tooltip = L10n.T("tooltip.maskImage");
            image.objectType = typeof(Texture2D);
            image.RegisterValueChangedCallback(e =>
            {
                if (e.target != image) return;
                var reference = TextureInfo.ToRef(e.newValue as Texture);
                DocumentCommands.EditSelected(L10n.T("undo.maskImage"), l =>
                {
                    l.mask.image = reference;
                    // 画像を入れたら、その画像がマスクになる。外したら選択へ戻る
                    if (!reference.IsEmpty) l.mask.source = MaskSource.Image;
                    else if (l.mask.source == MaskSource.Image) l.mask.source = MaskSource.Selection;
                });
            });
            UiHub.Watch(image, () =>
            {
                var l = DocumentHost.instance.SelectedLayer;
                if (l != null) image.SetValueWithoutNotify(l.mask.source == MaskSource.Image ? TextureInfo.Resolve(l.mask.image) : null);
            });

            var invert = root.Q<Toggle>("mask-invert");
            invert.text = L10n.T("mask.invert");
            DocBind.Layer(invert, "undo.mask", l => l.mask.invert, (l, v) => l.mask.invert = v);

            var maskOpacity = root.Q<Slider>("mask-opacity");
            maskOpacity.label = L10n.T("mask.opacity");
            DocBind.Layer(maskOpacity, "undo.mask", l => l.mask.opacity, (l, v) => l.mask.opacity = Mathf.Clamp01(v));

            // ぼかしは出力解像度での px で見せ、テクスチャ幅に対する比率で持つ（解像度を変えても見た目が変わらない）
            var feather = root.Q<Slider>("mask-feather");
            feather.label = L10n.T("mask.feather");
            feather.tooltip = L10n.T("tooltip.feather");
            DocBind.Layer(feather, "undo.mask",
                l => l.mask.feather * DocumentHost.instance.Doc.width,
                (l, v) => l.mask.feather = Mathf.Max(0f, v) / Mathf.Max(1, DocumentHost.instance.Doc.width));
        }

        private static string SourceLabel(MaskSource source)
        {
            switch (source)
            {
                case MaskSource.Image: return L10n.T("mask.source.image");
                case MaskSource.Embedded: return L10n.T("mask.source.embedded");
                default: return L10n.T("mask.source.selection");
            }
        }

        // ── 質感 ──

        private void BuildMaterialCard(VisualElement root)
        {
            root.Q<Label>("material-header").text = L10n.T("material.header");
            root.Q<Label>("fill-label").text = L10n.T("material.base");

            var fillColor = root.Q<Button>("fill-color");
            var fillTexture = root.Q<Button>("fill-texture");
            fillColor.text = L10n.T("term.color");
            fillTexture.text = L10n.T("term.texture");
            DocBind.Choice(new[] { (fillColor, FillKind.Color), (fillTexture, FillKind.Texture) },
                () => DocumentHost.instance.SelectedLayer?.fillKind ?? FillKind.Color,
                v => DocumentCommands.EditSelected(L10n.T("undo.fill"), l => l.fillKind = v));

            var color = root.Q<ColorField>("base-color");
            color.label = L10n.T("material.color");
            DocBind.Layer(color, "undo.fill", l => l.color, (l, v) => l.color = v);

            var texture = root.Q<ObjectField>("base-texture");
            texture.label = L10n.T("term.texture");
            DocBind.LayerTexture(texture, "undo.fill", l => l.baseTexture, (l, v) => l.baseTexture = v);

            var tint = root.Q<ColorField>("base-tint");
            tint.label = L10n.T("material.tint");
            tint.tooltip = L10n.T("tooltip.tint");
            DocBind.Layer(tint, "undo.fill", l => l.tint, (l, v) => l.tint = v);

            root.Q<Label>("normal-label").text = L10n.T("material.normal");
            var none = root.Q<Button>("normal-none");
            var flatten = root.Q<Button>("normal-flatten");
            var map = root.Q<Button>("normal-texture");
            none.text = L10n.T("normal.none");
            none.tooltip = L10n.T("tooltip.normalNone");
            flatten.text = L10n.T("normal.flatten");
            flatten.tooltip = L10n.T("tooltip.normalFlatten");
            map.text = L10n.T("normal.map");
            map.tooltip = L10n.T("tooltip.normalMap");
            DocBind.Choice(new[] { (none, NormalKind.None), (flatten, NormalKind.Flatten), (map, NormalKind.Texture) },
                () => DocumentHost.instance.SelectedLayer?.normalKind ?? NormalKind.None,
                v => DocumentCommands.EditSelected(L10n.T("undo.normal"), l => l.normalKind = v));

            var normalMap = root.Q<ObjectField>("normal-map");
            normalMap.label = L10n.T("material.normalMap");
            DocBind.LayerTexture(normalMap, "undo.normal", l => l.normalTexture, (l, v) => l.normalTexture = v);

            var scale = root.Q<Slider>("normal-scale");
            scale.label = L10n.T("material.normalScale");
            DocBind.Layer(scale, "undo.normal", l => l.normalScale, (l, v) => l.normalScale = v);
        }

        // ── 配置 ──

        private void BuildPlacementCard(VisualElement root)
        {
            root.Q<Label>("placement-header").text = L10n.T("placement.header");
            root.Q<Label>("placement-label").text = L10n.T("placement.mode");

            var reset = DennokoIcons.Apply(root.Q<Button>("placement-reset"), DennokoIcons.Refresh, "↺", L10n.T("tooltip.resetPlacement"));
            reset.clicked += () => DocumentCommands.EditSelected(L10n.T("undo.placement"), l =>
            {
                l.baseXform = new UvTransform();
                l.normalXform = new UvTransform();
                l.syncNormal = true;
                l.decalCenter = new Vector2(0.5f, 0.5f);
                l.decalSize = new Vector2(0.25f, 0.25f);
                l.decalRotation = 0f;
            });

            var tiling = root.Q<Button>("placement-tiling");
            var decal = root.Q<Button>("placement-decal");
            tiling.text = L10n.T("placement.tiling");
            decal.text = L10n.T("placement.decal");
            decal.tooltip = L10n.T("tooltip.decal");
            DocBind.Choice(new[] { (tiling, PlacementMode.Tiling), (decal, PlacementMode.Decal) },
                () => DocumentHost.instance.SelectedLayer?.placementMode ?? PlacementMode.Tiling,
                v => DocumentCommands.EditSelected(L10n.T("undo.placement"), l => l.placementMode = v));

            Vector2Field V2(string name, string labelKey, Func<Layer, Vector2> get, Action<Layer, Vector2> set)
            {
                var f = root.Q<Vector2Field>(name);
                f.label = L10n.T(labelKey);
                DocBind.Layer(f, "undo.placement", get, set);
                return f;
            }

            Slider Angle(string name, Func<Layer, float> get, Action<Layer, float> set)
            {
                var s = root.Q<Slider>(name);
                s.label = L10n.T("placement.rotation");
                DocBind.Layer(s, "undo.placement", get, set);
                return s;
            }

            V2("base-tiling", "placement.tiling", l => l.baseXform.tiling, (l, v) => l.baseXform.tiling = v);
            V2("base-offset", "placement.offset", l => l.baseXform.offset, (l, v) => l.baseXform.offset = v);
            Angle("base-rotation", l => l.baseXform.rotation, (l, v) => l.baseXform.rotation = v);

            var sync = root.Q<Toggle>("sync-normal");
            sync.text = L10n.T("placement.syncNormal");
            DocBind.Layer(sync, "undo.placement", l => l.syncNormal, (l, v) =>
            {
                // 個別に切り替えた時は、今のベースの配置から始める
                if (!v && l.syncNormal) l.normalXform = l.baseXform.Clone();
                l.syncNormal = v;
            });

            V2("normal-tiling", "placement.normalTiling", l => l.normalXform.tiling, (l, v) => l.normalXform.tiling = v);
            V2("normal-offset", "placement.normalOffset", l => l.normalXform.offset, (l, v) => l.normalXform.offset = v);
            Angle("normal-rotation", l => l.normalXform.rotation, (l, v) => l.normalXform.rotation = v);

            V2("decal-center", "placement.decalCenter", l => l.decalCenter, (l, v) => l.decalCenter = v);
            V2("decal-size", "placement.decalSize", l => l.decalSize, (l, v) => l.decalSize = v);
            Angle("decal-rotation", l => l.decalRotation, (l, v) => l.decalRotation = v);
            root.Q<Label>("decal-hint").text = L10n.T("placement.decalHint");
        }

        // ── 下地 ──

        private void BuildBackgroundCard(VisualElement root)
        {
            root.Q<Label>("background-header").text = L10n.T("layers.background");
            root.Q<Label>("bg-base-label").text = L10n.T("material.base");
            root.Q<Label>("bg-normal-label").text = L10n.T("material.normal");

            var baseColor = root.Q<Button>("bg-base-color");
            var baseTexture = root.Q<Button>("bg-base-texture");
            baseColor.text = L10n.T("term.color");
            baseTexture.text = L10n.T("term.texture");
            DocBind.Choice(new[] { (baseColor, BackgroundBaseKind.Color), (baseTexture, BackgroundBaseKind.Texture) },
                () => DocumentHost.instance.Doc.background.baseKind,
                v => DocumentCommands.EditBackground(L10n.T("undo.background"), b => b.baseKind = v));

            var color = root.Q<ColorField>("bg-color");
            color.label = L10n.T("material.color");
            DocBind.Background(color, "undo.background", b => b.color, (b, v) => b.color = v);

            var texture = root.Q<ObjectField>("bg-texture");
            texture.tooltip = L10n.T("tooltip.bgTexture");
            DocBind.BackgroundTexture(texture, "undo.background", b => b.baseTexture, (b, v) => b.baseTexture = v);

            var flat = root.Q<Button>("bg-normal-flat");
            var normalTexture = root.Q<Button>("bg-normal-texture");
            flat.text = L10n.T("normal.flat");
            normalTexture.text = L10n.T("term.texture");
            DocBind.Choice(new[] { (flat, BackgroundNormalKind.Flat), (normalTexture, BackgroundNormalKind.Texture) },
                () => DocumentHost.instance.Doc.background.normalKind,
                v => DocumentCommands.EditBackground(L10n.T("undo.background"), b => b.normalKind = v));

            var normalMap = root.Q<ObjectField>("bg-normal-map");
            normalMap.tooltip = L10n.T("tooltip.bgNormal");
            DocBind.BackgroundTexture(normalMap, "undo.background", b => b.normalTexture, (b, v) => b.normalTexture = v);
        }

        // ── 表示の切り替え ──

        private void RefreshVisibility()
        {
            var host = DocumentHost.instance;
            bool working = SessionController.Current != null;
            var layer = working ? host.SelectedLayer : null;
            bool background = working && layer == null && host.SelectedLayerId == DocumentHost.BackgroundId;
            // 新しいレイヤーの範囲を選んでいる間は、設定を出さない（まだレイヤーが無い）
            bool show = layer != null && DtxModeController.Mode != DtxMode.SelectNew;

            Bind.Show(_root.Q("layer-card"), show);
            Bind.Show(_root.Q("material-card"), show);
            Bind.Show(_root.Q("background-card"), background && DtxModeController.Mode != DtxMode.SelectNew);

            if (background)
            {
                var bg = host.Doc.background;
                Bind.Show(_root.Q("bg-color"), bg.baseKind == BackgroundBaseKind.Color);
                Bind.Show(_root.Q("bg-texture"), bg.baseKind == BackgroundBaseKind.Texture);
                Bind.Show(_root.Q("bg-normal-map"), bg.normalKind == BackgroundNormalKind.Texture);
            }

            bool usesTexture = layer != null && (layer.fillKind == FillKind.Texture || layer.normalKind == NormalKind.Texture);
            Bind.Show(_root.Q("placement-card"), show && usesTexture);
            if (layer == null) return;

            Bind.Show(_root.Q("mask-image"), layer.mask.source != MaskSource.Embedded);
            Bind.Show(_root.Q("base-color"), layer.fillKind == FillKind.Color);
            Bind.Show(_root.Q("base-texture"), layer.fillKind == FillKind.Texture);
            Bind.Show(_root.Q("base-tint"), layer.fillKind == FillKind.Texture);
            Bind.Show(_root.Q("normal-map"), layer.normalKind == NormalKind.Texture);
            Bind.Show(_root.Q("normal-scale"), layer.normalKind == NormalKind.Texture);

            bool decal = layer.placementMode == PlacementMode.Decal;
            Bind.Show(_root.Q("tiling-group"), !decal);
            Bind.Show(_root.Q("decal-group"), decal);
            // ノーマルマップを使う時だけ「同じ配置にする」を出し、外したらノーマル用の配置を出す
            bool normalMap = layer.normalKind == NormalKind.Texture;
            Bind.Show(_root.Q("sync-normal"), normalMap);
            Bind.Show(_root.Q("normal-xform-group"), normalMap && !layer.syncNormal);
        }
    }
}
