using System;
using System.Collections.Generic;
using Dennoko.DTXTool.Core;
using UnityEditor;
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
    /// 選択中のレイヤー（または下地）の設定カード。名前・ブレンド・不透明度はレイヤー一覧の下（LayersCard）にある。
    ///   マスク : 範囲の再調整（大きいボタン）とマスク画像。反転・不透明度・ぼかしは「▶ 詳細設定」に畳む
    ///   質感   : 色とテクスチャ（両方効く: テクスチャ × 色）、ノーマルマップと「下地の凹凸を消す」。
    ///            配置はそれぞれのテクスチャ欄のすぐ下に、テクスチャを入れた時だけ出す
    /// 使わない欄は隠す（グレーアウトにしない）。
    /// </summary>
    internal sealed class LayerInspector
    {
        private const string MaskDetailsOpenKey = "DTXTool.MaskDetailsOpen";

        private readonly VisualElement _root;

        public LayerInspector(VisualElement root)
        {
            _root = root;
            BuildMaskCard(root);
            BuildMaterialCard(root);
            BuildPlacement(root);
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

        // ── マスク ──

        private void BuildMaskCard(VisualElement root)
        {
            root.Q<Label>("layer-header").text = L10n.T("mask.title");

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
                bool selection = l.mask.source == MaskSource.Selection;
                reselect.text = editing ? L10n.T("button.confirmArea")
                    : selection ? L10n.T("button.reselectArea") : L10n.T("button.toSelection");
                reselect.tooltip = editing ? string.Empty
                    : selection ? L10n.T("tooltip.reselect") : L10n.T("tooltip.toSelection");
                Bind.Active(reselect, editing);
                // 範囲が選択以外から来ている時だけ、どこから来たかを書く（画像はマスク画像の欄で分かるので PSD の時だけ）
                source.text = l.mask.source == MaskSource.Embedded ? L10n.T("mask.embeddedHint") : string.Empty;
                Bind.Show(source, l.mask.source == MaskSource.Embedded && !editing);
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

            // 反転・不透明度・ぼかしは「▶ 詳細設定」に畳む（開閉はエディターを閉じるまで覚える）
            var details = root.Q("mask-details");
            root.Q<Label>("mask-details-title").text = L10n.T("details.options");
            DennokoBlockUI.BindDetails(details, SessionState.GetBool(MaskDetailsOpenKey, false), open => SessionState.SetBool(MaskDetailsOpenKey, open));

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

            // 閉じている間は、初期値から変えている項目だけを見出し行にチップで出す
            var chips = root.Q("mask-details-chips");
            UiHub.Watch(chips, () =>
            {
                chips.Clear();
                var l = DocumentHost.instance.SelectedLayer;
                if (l == null) return;
                if (l.mask.invert) chips.Add(DennokoBlockUI.Chip(null, L10n.T("mask.invert")));
                if (l.mask.opacity < 0.999f) chips.Add(DennokoBlockUI.Chip(null, L10n.T("mask.opacity") + " " + Mathf.RoundToInt(l.mask.opacity * 100f) + "%"));
                int px = DocumentOps.FeatherPixels(l.mask, DocumentHost.instance.Doc.width);
                if (px > 0) chips.Add(DennokoBlockUI.Chip(null, L10n.T("mask.feather") + " " + px + "px"));
            });
        }

        // ── 質感 ──

        private void BuildMaterialCard(VisualElement root)
        {
            root.Q<Label>("material-header").text = L10n.T("material.header");

            var color = root.Q<ColorField>("base-color");
            color.label = L10n.T("material.color");
            color.tooltip = L10n.T("tooltip.baseColor");
            DocBind.Layer(color, "undo.fill", l => l.color, (l, v) => l.color = v);

            var texture = root.Q<ObjectField>("base-texture");
            texture.label = L10n.T("term.texture");
            texture.tooltip = L10n.T("tooltip.baseTexture");
            DocBind.LayerTexture(texture, "undo.fill", l => l.baseTexture, (l, v) =>
            {
                // 色はテクスチャに掛かるので、テクスチャを初めて入れた時は白にして、テクスチャの色をそのまま見せる
                // （新しいレイヤーには見分け用の色が付いている）。同じ Undo の中なので戻せる
                if (!l.HasBaseTexture && !v.IsEmpty) l.color = new Color(1f, 1f, 1f, l.color.a);
                l.baseTexture = v;
            });

            var normalMap = root.Q<ObjectField>("normal-map");
            normalMap.label = L10n.T("material.normal");
            normalMap.tooltip = L10n.T("tooltip.normalMap");
            DocBind.LayerTexture(normalMap, "undo.normal", l => l.normalTexture, (l, v) => l.normalTexture = v);

            var scale = root.Q<Slider>("normal-scale");
            scale.label = L10n.T("material.normalScale");
            DocBind.Layer(scale, "undo.normal", l => l.normalScale, (l, v) => l.normalScale = v);

            var replace = root.Q<Toggle>("replace-normal");
            replace.text = L10n.T("normal.replace");
            replace.tooltip = L10n.T("tooltip.normalReplace");
            DocBind.Layer(replace, "undo.normal", l => l.replaceNormal, (l, v) => l.replaceNormal = v);

            var normalMask = root.Q<ObjectField>("normal-mask");
            normalMask.label = L10n.T("material.normalMask");
            normalMask.tooltip = L10n.T("tooltip.normalMask");
            DocBind.LayerTexture(normalMask, "undo.normal", l => l.normalMask, (l, v) => l.normalMask = v);
        }

        // ── 配置（メインテクスチャ・ノーマルそれぞれのテクスチャ欄の下） ──

        private void BuildPlacement(VisualElement root)
        {
            void Reset(Layer l)
            {
                l.baseXform = new UvTransform();
                l.normalXform = new UvTransform();
                l.syncNormal = false;
                l.decalCenter = new Vector2(0.5f, 0.5f);
                l.decalSize = new Vector2(0.25f, 0.25f);
                l.decalRotation = 0f;
            }

            // 方式（タイリング / デカール）はレイヤーに 1 つ。ベースのテクスチャが無い時はノーマル側に出す
            void ModeRow(string label, string tiling, string decal, string reset)
            {
                root.Q<Label>(label).text = L10n.T("placement.header");
                var t = root.Q<Button>(tiling);
                var d = root.Q<Button>(decal);
                t.text = L10n.T("placement.tiling");
                d.text = L10n.T("placement.decal");
                d.tooltip = L10n.T("tooltip.decal");
                DocBind.Choice(new[] { (t, PlacementMode.Tiling), (d, PlacementMode.Decal) },
                    () => DocumentHost.instance.SelectedLayer?.placementMode ?? PlacementMode.Tiling,
                    v => DocumentCommands.EditSelected(L10n.T("undo.placement"), l => l.placementMode = v));
                var r = DennokoIcons.Apply(root.Q<Button>(reset), Icons.Refresh, "↺", L10n.T("tooltip.resetPlacement"));
                r.clicked += () => DocumentCommands.EditSelected(L10n.T("undo.placement"), Reset);
            }
            ModeRow("placement-label", "placement-tiling", "placement-decal", "placement-reset");
            ModeRow("normal-placement-label", "normal-placement-tiling", "normal-placement-decal", "normal-placement-reset");

            void V2(string name, string labelKey, Func<Layer, Vector2> get, Action<Layer, Vector2> set)
            {
                var f = root.Q<Vector2Field>(name);
                f.label = L10n.T(labelKey);
                DocBind.Layer(f, "undo.placement", get, set);
            }

            void Angle(string name, Func<Layer, float> get, Action<Layer, float> set)
            {
                var s = root.Q<Slider>(name);
                s.label = L10n.T("placement.rotation");
                DocBind.Layer(s, "undo.placement", get, set);
            }

            // メインテクスチャ
            V2("base-tiling", "placement.tiling", l => l.baseXform.tiling, (l, v) => l.baseXform.tiling = v);
            V2("base-offset", "placement.offset", l => l.baseXform.offset, (l, v) => l.baseXform.offset = v);
            Angle("base-rotation", l => l.baseXform.rotation, (l, v) => l.baseXform.rotation = v);
            V2("decal-center", "placement.decalCenter", l => l.decalCenter, (l, v) => l.decalCenter = v);
            V2("decal-size", "placement.decalSize", l => l.decalSize, (l, v) => l.decalSize = v);
            Angle("decal-rotation", l => l.decalRotation, (l, v) => l.decalRotation = v);

            // ノーマル（デカールの位置はレイヤーに 1 つなので、ベースのテクスチャが無い時だけここに出す）
            V2("normal-tiling", "placement.tiling", l => l.normalXform.tiling, (l, v) => l.normalXform.tiling = v);
            V2("normal-offset", "placement.offset", l => l.normalXform.offset, (l, v) => l.normalXform.offset = v);
            Angle("normal-rotation", l => l.normalXform.rotation, (l, v) => l.normalXform.rotation = v);
            V2("normal-decal-center", "placement.decalCenter", l => l.decalCenter, (l, v) => l.decalCenter = v);
            V2("normal-decal-size", "placement.decalSize", l => l.decalSize, (l, v) => l.decalSize = v);
            Angle("normal-decal-rotation", l => l.decalRotation, (l, v) => l.decalRotation = v);

            var sync = root.Q<Toggle>("sync-normal");
            sync.text = L10n.T("placement.syncNormal");
            DocBind.Layer(sync, "undo.placement", l => l.syncNormal, (l, v) =>
            {
                // 個別に戻した時は、今のベースの配置から始める
                if (!v && l.syncNormal) l.normalXform = l.baseXform.Clone();
                l.syncNormal = v;
            });
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
            bool baseOnly = host.Doc.editMode == EditMode.BaseOnly;
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
                Bind.Show(_root.Q("bg-normal-row"), !baseOnly);
                Bind.Show(_root.Q("bg-normal-map"), !baseOnly && bg.normalKind == BackgroundNormalKind.Texture);
            }
            if (layer == null) return;

            Bind.Show(_root.Q("mask-image"), layer.mask.source != MaskSource.Embedded);

            bool baseTex = layer.HasBaseTexture;
            bool normalTex = layer.HasNormalTexture;
            bool decal = layer.placementMode == PlacementMode.Decal;

            // メインテクスチャの配置
            Bind.Show(_root.Q("base-placement"), baseTex);
            Bind.Show(_root.Q("tiling-group"), !decal);
            Bind.Show(_root.Q("decal-group"), decal);

            // ノーマル関連（ベースのみモードの時は丸ごと隠してコンパクトにする）
            Bind.Show(_root.Q("normal-map"), !baseOnly);
            Bind.Show(_root.Q("normal-options"), !baseOnly && normalTex);
            Bind.Show(_root.Q("replace-normal"), !baseOnly);
            Bind.Show(_root.Q("normal-mask"), !baseOnly && (layer.AffectsNormal || layer.HasNormalMask));
            Bind.Show(_root.Q("normal-mode-row"), !baseTex);
            Bind.Show(_root.Q("normal-decal-group"), decal && !baseTex);
            // 「同じ配置にする」はベースのテクスチャをタイリングしている時だけ意味がある。オンなら個別の欄は隠す
            bool canSync = baseTex && !decal;
            Bind.Show(_root.Q("sync-normal"), canSync);
            Bind.Show(_root.Q("normal-xform-group"), !decal && !(canSync && layer.syncNormal));
        }
    }
}
