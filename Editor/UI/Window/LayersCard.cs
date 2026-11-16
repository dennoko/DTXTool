using System;
using System.Collections.Generic;
using System.Text;
using Dennoko.DTXTool.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Background = Dennoko.DTXTool.Core.Background;

namespace Dennoko.DTXTool
{
    /// <summary>レイヤーに対する操作（一覧のボタン・ドロップ・ショートカットから呼ぶ）。結果の通知まで含める。</summary>
    internal static class LayerActions
    {
        private static DocumentHost H => DocumentHost.instance;

        /// <summary>マスク画像からレイヤーを作る（外部で作ったマスクのドラッグ＆ドロップ）。</summary>
        public static void AddImageMaskLayer(Texture2D mask, MessageOrigin origin)
        {
            if (SessionController.Current == null || mask == null) return;
            var reference = TextureInfo.ToRef(mask);
            if (reference.IsEmpty)
            {
                Notifier.Post(MessageLevel.Warning, L10n.T("status.notAsset"), origin);
                return;
            }
            var layer = DocumentCommands.AddImageMaskLayer(L10n.T("undo.addLayer"), mask.name, reference);
            layer.color = DtxModeController.NextLayerColor(H.Doc.layers.Count);
            Notifier.Post(MessageLevel.Success, L10n.F("status.layerAdded", layer.name), origin);
        }

        public static void DuplicateSelected()
        {
            if (H.SelectedLayer == null) return;
            DocumentCommands.Duplicate(L10n.T("undo.duplicateLayer"), H.SelectedLayerId);
        }

        public static void DeleteSelected()
        {
            if (H.SelectedLayer == null) return;
            if (DtxModeController.Mode == DtxMode.Reselect && DtxModeController.EditingLayerId == H.SelectedLayerId)
                DtxModeController.Cancel();
            DocumentCommands.Delete(L10n.T("undo.deleteLayer"), H.SelectedLayerId);
        }

        /// <summary>一覧の上（＝合成の上）へ動かすなら up = true。</summary>
        public static void MoveSelected(bool up)
        {
            int i = H.Doc.IndexOf(H.SelectedLayerId);
            if (i < 0) return;
            DocumentCommands.Move(L10n.T("undo.moveLayer"), H.SelectedLayerId, i + (up ? 1 : -1));
        }

        public static void ToggleVisible(string layerId)
            => DocumentCommands.EditLayer(layerId, L10n.T("undo.visibility"), l => l.visible = !l.visible);
    }

    /// <summary>
    /// レイヤー一覧のカード。上が最上段、最下段は下地（固定）。
    /// 行はクリックで選択。行の左の目で表示切替。追加・複製・順序・削除は見出し行のアイコンボタン。
    /// 行は内容が変わった時だけ作り直す（スライダー操作のたびに作り直さない）。
    /// </summary>
    internal sealed class LayersCard
    {
        private readonly VisualElement _list;
        private readonly VisualElement _empty;
        private readonly Button _duplicate, _up, _down, _delete;
        private string _builtKey;

        public LayersCard(VisualElement root)
        {
            _list = root.Q("layer-list");
            _empty = root.Q("layers-empty");
            root.Q<Label>("layers-header").text = L10n.T("layers.header");
            root.Q<Label>("layers-empty-message").text = L10n.T("layers.empty");

            var add = DennokoIcons.Apply(root.Q<Button>("layer-add"), DennokoIcons.Plus, "+", L10n.T("tooltip.addLayer"));
            add.clicked += DtxModeController.BeginSelectNew;
            var emptyAction = root.Q<Button>("layers-empty-action");
            emptyAction.text = L10n.T("button.selectArea");
            emptyAction.clicked += DtxModeController.BeginSelectNew;

            _duplicate = DennokoIcons.Apply(root.Q<Button>("layer-duplicate"), Icons.Duplicate, "D", L10n.T("tooltip.duplicateLayer"));
            _duplicate.clicked += LayerActions.DuplicateSelected;
            _up = DennokoIcons.Apply(root.Q<Button>("layer-up"), Icons.MoveUp, "▲", L10n.T("tooltip.layerUp"));
            _up.clicked += () => LayerActions.MoveSelected(true);
            _down = DennokoIcons.Apply(root.Q<Button>("layer-down"), Icons.MoveDown, "▼", L10n.T("tooltip.layerDown"));
            _down.clicked += () => LayerActions.MoveSelected(false);
            _delete = DennokoIcons.Apply(root.Q<Button>("layer-delete"), Icons.Clear, "×", L10n.T("tooltip.deleteLayer"));
            _delete.clicked += LayerActions.DeleteSelected;

            // テクスチャを一覧へ落とすと、マスク画像のレイヤーを作る
            var card = root.Q("layers-card");
            card.RegisterCallback<DragUpdatedEvent>(e =>
            {
                if (SessionController.Current == null || DraggedTexture() == null) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            });
            card.RegisterCallback<DragPerformEvent>(e =>
            {
                var tex = DraggedTexture();
                if (tex == null) return;
                DragAndDrop.AcceptDrag();
                LayerActions.AddImageMaskLayer(tex, MessageOrigin.Window);
            });

            UiHub.Watch(card, Refresh);
        }

        private static Texture2D DraggedTexture()
        {
            foreach (var o in DragAndDrop.objectReferences)
                if (o is Texture2D t) return t;
            return null;
        }

        private void Refresh()
        {
            var host = DocumentHost.instance;
            var doc = host.Doc;
            bool working = SessionController.Current != null;
            if (!working) return;

            int index = doc.IndexOf(host.SelectedLayerId);
            bool hasLayer = index >= 0;
            _duplicate.SetEnabled(hasLayer);
            _delete.SetEnabled(hasLayer);
            _up.SetEnabled(hasLayer && index < doc.layers.Count - 1);
            _down.SetEnabled(hasLayer && index > 0);
            Bind.Show(_empty, doc.layers.Count == 0 && !DtxModeController.IsSelecting);

            string key = BuildKey(doc, host.SelectedLayerId);
            if (key == _builtKey) return;
            _builtKey = key;

            _list.Clear();
            for (int i = doc.layers.Count - 1; i >= 0; i--) _list.Add(MakeRow(doc.layers[i], host.SelectedLayerId));
            _list.Add(MakeBackgroundRow(doc.background, host.SelectedLayerId == DocumentHost.BackgroundId));
        }

        /// <summary>行の見た目を決める内容だけを並べた文字列（これが変わった時だけ作り直す）。</summary>
        private static string BuildKey(DtxDocument doc, string selected)
        {
            var sb = new StringBuilder(256);
            sb.Append(selected).Append('|').Append(L10n.Japanese ? 'j' : 'e').Append('|')
              .Append(DtxModeController.Mode).Append(DtxModeController.EditingLayerId).Append('|');
            foreach (var l in doc.layers)
            {
                sb.Append(l.id).Append(':').Append(l.name).Append(':').Append(l.visible ? '1' : '0').Append(':')
                  .Append((int)l.fillKind).Append(ColorUtility.ToHtmlStringRGB(l.fillKind == FillKind.Color ? l.color : l.tint)).Append(':')
                  .Append(l.baseTexture.guid).Append(':').Append((int)l.blendMode).Append((int)l.mask.source).Append((int)l.normalKind).Append(';');
            }
            var bg = doc.background;
            sb.Append((int)bg.baseKind).Append(ColorUtility.ToHtmlStringRGB(bg.color)).Append(bg.baseTexture.guid).Append((int)bg.normalKind);
            return sb.ToString();
        }

        private static VisualElement MakeRow(Layer layer, string selectedId)
        {
            string id = layer.id;
            var row = new Button { text = string.Empty };
            row.AddToClassList("dennoko-candidate");
            row.AddToClassList("dtx-layer-row");
            row.clickable = new Clickable(() => DocumentCommands.Select(id));
            row.RegisterCallback<ClickEvent>(e =>
            {
                // ダブルクリックで範囲の再調整
                if (e.clickCount >= 2) DtxModeController.BeginReselect(id);
            });

            var eye = new Button { text = string.Empty, tooltip = L10n.T("tooltip.visibility") };
            eye.AddToClassList("dennoko-icon-button");
            eye.AddToClassList("dtx-layer-row__eye");
            DennokoIcons.Apply(eye, layer.visible ? Icons.XRay : Icons.Isolate, layer.visible ? "●" : "○", L10n.T("tooltip.visibility"));
            eye.clickable = new Clickable(() => LayerActions.ToggleVisible(id));
            // 目のクリックで行の選択が変わらないようにする
            eye.RegisterCallback<ClickEvent>(e => e.StopPropagation());
            row.Add(eye);

            row.Add(MakeSwatch(layer.fillKind == FillKind.Texture ? TextureInfo.Resolve(layer.baseTexture) : null,
                layer.fillKind == FillKind.Color ? layer.color : layer.tint));

            var name = new Label(layer.name) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("dennoko-candidate__name");
            row.Add(name);

            var sub = new Label(SubText(layer)) { pickingMode = PickingMode.Ignore };
            sub.AddToClassList("dennoko-candidate__sub");
            row.Add(sub);

            row.EnableInClassList("dtx-layer-row--hidden", !layer.visible);
            Bind.Active(row, id == selectedId);
            return row;
        }

        private static VisualElement MakeBackgroundRow(Background bg, bool selected)
        {
            var row = new Button { text = string.Empty };
            row.AddToClassList("dennoko-candidate");
            row.AddToClassList("dtx-layer-row");
            row.AddToClassList("dtx-layer-row--background");
            row.clickable = new Clickable(() => DocumentCommands.Select(DocumentHost.BackgroundId));

            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.AddToClassList("dtx-layer-row__eye");
            row.Add(spacer);
            row.Add(MakeSwatch(bg.baseKind == BackgroundBaseKind.Texture ? TextureInfo.Resolve(bg.baseTexture) : null, bg.color));

            var name = new Label(L10n.T("layers.background")) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("dennoko-candidate__name");
            row.Add(name);
            var sub = new Label(bg.baseKind == BackgroundBaseKind.Texture ? L10n.T("term.texture") : L10n.T("term.color")) { pickingMode = PickingMode.Ignore };
            sub.AddToClassList("dennoko-candidate__sub");
            row.Add(sub);
            Bind.Active(row, selected);
            return row;
        }

        /// <summary>質感の見本（色、またはテクスチャの縮小表示）。色はデータなので C# から入れる。</summary>
        private static VisualElement MakeSwatch(Texture2D texture, Color color)
        {
            var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
            swatch.AddToClassList("dtx-swatch");
            if (texture != null)
            {
                swatch.style.backgroundImage = texture;
                swatch.style.unityBackgroundImageTintColor = new Color(color.r, color.g, color.b, 1f);
            }
            else
            {
                swatch.style.backgroundColor = new Color(color.r, color.g, color.b, 1f);
            }
            return swatch;
        }

        private static string SubText(Layer layer)
        {
            var parts = new List<string>(3);
            if (DtxModeController.Mode == DtxMode.Reselect && DtxModeController.EditingLayerId == layer.id) parts.Add(L10n.T("layers.reselecting"));
            if (layer.blendMode != BlendMode.Normal) parts.Add(LayerInspector.BlendLabel(layer.blendMode));
            if (layer.mask.source == MaskSource.Image) parts.Add(L10n.T("mask.source.image"));
            else if (layer.mask.source == MaskSource.Embedded) parts.Add(L10n.T("mask.source.embedded"));
            if (layer.normalKind != NormalKind.None) parts.Add("N");
            return string.Join(" · ", parts);
        }
    }
}
