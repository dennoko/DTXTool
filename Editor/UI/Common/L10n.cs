using System.Collections.Generic;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>表示文字列（ja / en）。用語は UI 設計 §2.6.5 に固定する。</summary>
    public static class L10n
    {
        private static readonly Dictionary<string, (string ja, string en)> Table = new Dictionary<string, (string, string)>
        {
            // 用語
            { "term.island", ("島", "Island") },
            { "term.part", ("パーツ", "Part") },
            { "term.face", ("面", "Face") },
            { "term.select", ("選択", "Select") },
            { "term.paint", ("ペイント", "Paint") },
            { "term.brush", ("ブラシ", "Brush") },
            { "term.rect", ("矩形", "Rect") },
            { "term.lasso", ("投げ縄", "Lasso") },
            { "term.eraser", ("消しゴム", "Eraser") },
            { "term.symmetry", ("対称", "Symmetry") },
            { "term.xray", ("X-Ray", "X-Ray") },
            { "term.isolate", ("孤立表示", "Isolate") },
            { "term.pause", ("一時停止", "Pause") },
            { "term.undo", ("元に戻す", "Undo") },
            { "term.redo", ("やり直す", "Redo") },
            { "term.clear", ("クリア", "Clear") },
            { "term.selectAll", ("全選択", "Select all") },
            { "term.invert", ("反転", "Invert") },
            { "term.export", ("出力", "Export") },
            { "term.apply", ("適用", "Apply") },
            { "term.all", ("全体", "All") },

            // ── DTXTool ──
            { "prefs.previewSize", ("プレビューの最大解像度", "Maximum preview size") },
            { "project.edgePadding", ("エッジ拡張の幅（px、-1 = 自動）", "Edge padding (px, -1 = auto)") },
            { "term.color", ("単色", "Color") },
            { "term.texture", ("テクスチャ", "Texture") },
            { "blend.normal", ("通常", "Normal") },
            { "blend.multiply", ("乗算", "Multiply") },
            { "blend.additive", ("加算", "Additive") },
            { "blend.screen", ("スクリーン", "Screen") },
            { "blend.overlay", ("オーバーレイ", "Overlay") },
            { "button.confirm", ("確定", "Confirm") },
            { "button.confirmSelection", ("選択を確定", "Confirm selection") },
            { "button.exportPng", ("PNG 出力", "Export PNG") },
            { "button.exportPsd", ("PSD 保存", "Save PSD") },
            { "button.reselectArea", ("範囲を再調整", "Adjust area") },
            { "button.confirmArea", ("範囲を確定", "Confirm area") },
            { "psd.header", ("PSD", "PSD") },
            { "details.title", ("詳細", "Details") },
            { "details.options", ("詳細設定", "Options") },
            { "mask.embeddedHint", ("範囲は PSD から復元した画像です（メッシュが違うため、選択では直せません）", "The area was restored from the PSD as an image (the mesh differs, so it cannot be adjusted by selection)") },
            { "normal.replace", ("下地の凹凸を消す", "Remove the bumps underneath") },
            { "tooltip.normalReplace", ("オン: この範囲の下のノーマルを消して、ノーマルマップで置き換える（マップが無ければ平らにする）。\nオフ: ノーマルマップを下のノーマルに重ねる。", "On: remove the normal underneath in this area and replace it with the normal map (flat if there is no map).\nOff: layer the normal map over the normal underneath.") },
            { "tooltip.baseColor", ("テクスチャに掛ける色（テクスチャが無ければこの色で塗る）", "Color multiplied with the texture (fills with this color when there is no texture)") },
            { "tooltip.baseTexture", ("このレイヤーに貼るテクスチャ。上の色が掛かります。", "Texture for this layer. The color above is multiplied in.") },
            { "button.importPsd", ("PSD 読み込み", "Load PSD") },
            { "button.reselect", ("再調整", "Adjust") },
            { "button.toSelection", ("選択し直す", "Reselect") },
            { "button.selectArea", ("範囲を選択", "Select area") },
            { "target.propAuto", ("自動（{0}）", "Auto ({0})") },
            { "target.propNone", ("なし", "none") },
            { "tooltip.propBase", ("プレビューと出力の適用先になる BaseMap 用のシェーダープロパティ。自動判別が合わない時だけ差し替えます。", "Shader property used for the BaseMap preview and apply. Change only when auto-detection is wrong.") },
            { "tooltip.propNormal", ("プレビューと出力の適用先になる NormalMap 用のシェーダープロパティ。自動判別が合わない時だけ差し替えます。", "Shader property used for the NormalMap preview and apply. Change only when auto-detection is wrong.") },
            { "select.title.new", ("新しいレイヤーの範囲を選択中", "Selecting the area for a new layer") },
            { "select.title.reselect", ("「{0}」の範囲を再調整中", "Adjusting the area of '{0}'") },
            { "select.hint", ("シーンのモデル、または下の UV ビューをクリックして範囲を選びます。", "Click the model in the Scene view, or the UV view below, to select the area.") },
            { "select.key.add", ("追加", "add") },
            { "select.key.remove", ("除外", "remove") },
            { "select.key.confirm", ("確定", "confirm") },
            { "select.key.cancel", ("取消", "cancel") },
            { "layers.header", ("レイヤー", "Layers") },
            { "layers.background", ("下地", "Background") },
            { "layers.empty", ("範囲を選んで最初のレイヤーを作ります", "Select an area to create the first layer") },
            { "layers.reselecting", ("再調整中", "adjusting") },
            { "layer.defaultName", ("レイヤー", "Layer") },
            { "tooltip.addLayer", ("範囲を選んで新しいレイヤーを作る", "Select an area and create a new layer") },
            { "tooltip.duplicateLayer", ("レイヤーを複製", "Duplicate layer") },
            { "tooltip.layerUp", ("上へ", "Move up") },
            { "tooltip.layerDown", ("下へ", "Move down") },
            { "tooltip.deleteLayer", ("レイヤーを削除", "Delete layer") },
            { "tooltip.visibility", ("表示 / 非表示", "Show / hide") },
            { "layer.header", ("レイヤー設定", "Layer") },
            { "layer.name", ("名前", "Name") },
            { "layer.blend", ("ブレンド", "Blend") },
            { "layer.opacity", ("不透明度", "Opacity") },
            { "mask.title", ("マスク", "Mask") },
            { "mask.source", ("範囲", "Area") },
            { "mask.source.selection", ("選択した範囲", "Selected area") },
            { "mask.source.image", ("マスク画像", "Mask image") },
            { "mask.source.embedded", ("PSD から復元", "Restored from PSD") },
            { "mask.image", ("マスク画像", "Mask image") },
            { "mask.invert", ("反転", "Invert") },
            { "mask.opacity", ("不透明度", "Opacity") },
            { "mask.feather", ("ぼかし", "Feather") },
            { "tooltip.reselect", ("シーン上でクリックして、このレイヤーの範囲を追加・除外する", "Click in the Scene to add to or remove from this layer's area") },
            { "tooltip.toSelection", ("マスクを捨てて、シーン上で範囲を選び直す", "Discard the mask and select the area in the Scene") },
            { "tooltip.maskImage", ("外部で作ったマスク画像（白 = 適用する範囲）。ここへドラッグ＆ドロップできます。", "A mask image made elsewhere (white = applied). Drag and drop here.") },
            { "tooltip.feather", ("マスクの縁をぼかす幅（出力解像度での px）", "Width of the soft edge (px at the output resolution)") },
            { "material.header", ("質感", "Material") },
            { "material.base", ("ベース", "Base") },
            { "material.color", ("色", "Color") },
            { "material.tint", ("Tint", "Tint") },
            { "material.normal", ("ノーマル", "Normal") },
            { "material.normalMap", ("ノーマルマップ", "Normal map") },
            { "material.normalScale", ("強さ", "Strength") },
            { "normal.none", ("なし", "None") },
            { "normal.flatten", ("平坦化", "Flatten") },
            { "normal.map", ("マップ", "Map") },
            { "normal.flat", ("平坦", "Flat") },
            { "tooltip.tint", ("テクスチャに乗算する色", "Color multiplied with the texture") },
            { "tooltip.normalNone", ("下のノーマルをそのまま通す", "Keep the normal underneath") },
            { "tooltip.normalFlatten", ("この範囲のノーマルを平らにする", "Flatten the normal in this area") },
            { "tooltip.normalMap", ("このレイヤーのノーマルマップ（Normal map / Default どちらのインポート設定でも可）", "Normal map for this layer (imported as Normal map or Default)") },
            { "placement.header", ("配置", "Placement") },
            { "placement.mode", ("方式", "Mode") },
            { "placement.tiling", ("タイリング", "Tiling") },
            { "placement.decal", ("デカール", "Decal") },
            { "placement.offset", ("オフセット", "Offset") },
            { "placement.rotation", ("回転", "Rotation") },
            { "placement.syncNormal", ("ノーマルも同じ配置にする", "Use the same placement for the normal") },
            { "placement.normalTiling", ("タイリング（N）", "Tiling (N)") },
            { "placement.normalOffset", ("オフセット（N）", "Offset (N)") },
            { "placement.decalCenter", ("位置（UV）", "Position (UV)") },
            { "placement.decalSize", ("大きさ（UV）", "Size (UV)") },
            { "placement.decalHint", ("UV ビュー上でドラッグ = 移動、Shift + ホイール = 大きさ、Ctrl + ホイール = 回転", "In the UV view: drag = move, Shift + wheel = size, Ctrl + wheel = rotate") },
            { "tooltip.decal", ("UV 上の 1 か所にだけ貼る", "Place once at a position in UV space") },
            { "tooltip.resetPlacement", ("配置を初期値に戻す", "Reset placement") },
            { "tooltip.bgTexture", ("下地として読み込む既存の BaseMap", "Existing BaseMap used as the background") },
            { "tooltip.bgNormal", ("下地として読み込む既存の NormalMap", "Existing NormalMap used as the background") },
            { "output.name", ("ファイル名", "File name") },
            { "output.psdHint", ("PSD には続きを再開するための情報が入ります。外部ソフトで上書き保存すると再開できなくなることがあるので、DTXTool が出力した PSD を残してください。", "The PSD carries the data needed to resume editing. Re-saving it in another app may drop that data, so keep the PSD exported by DTXTool.") },
            { "tooltip.outputName", ("出力ファイル名の先頭（空にすると対象の名前）。_BaseMap / _NormalMap が付きます。", "Prefix of the output files (empty = target name). _BaseMap / _NormalMap are appended.") },
            { "tooltip.exportPsd", ("BaseMap と NormalMap のレイヤーを 1 つの PSD に出力（続きの再開にも使えます）", "Export BaseMap and NormalMap layers into one PSD (also used to resume later)") },
            { "tooltip.importPsd", ("DTXTool が出力した PSD を読み込んで続きから再開", "Load a PSD exported by DTXTool and resume editing") },
            { "status.baking", ("ベイク中…", "Baking…") },
            { "status.writing", ("書き込み中…", "Writing…") },
            { "status.exported", ("出力: {0} / {1}", "Exported: {0} / {1}") },
            { "status.layerAdded", ("レイヤー「{0}」を追加", "Added layer '{0}'") },
            { "status.reselected", ("範囲を更新", "Area updated") },
            { "status.selectionEmpty", ("範囲が選ばれていません", "Nothing is selected") },
            { "status.notAsset", ("プロジェクト内のテクスチャを指定してください", "Use a texture asset in the project") },
            { "status.meshChangedLayers", ("メッシュが変わったため、{0} 枚のレイヤーの範囲を復元できません", "Mesh changed: the area of {0} layer(s) could not be restored") },
            { "status.previewRecovered", ("前回のプレビューが正常終了しなかったため、{0} 件のマテリアルを元のテクスチャへ戻しました。", "The previous preview did not end normally; restored the original textures of {0} material(s).") },
            { "status.psdWriting", ("PSD を書き込み中…", "Writing PSD…") },
            { "status.psdExported", ("PSD 出力: {0}", "PSD exported: {0}") },
            { "status.psdWriteFailed", ("PSD を書き込めません（{0}）", "Cannot write PSD ({0})") },
            { "status.psdLoaded", ("PSD を読み込み: {0}", "PSD loaded: {0}") },
            { "status.psdLoadedMeshChanged", ("PSD を読み込み: {0}（メッシュが違うため {1} 枚は範囲を画像として復元）", "PSD loaded: {0} (mesh differs; {1} layer area(s) restored as images)") },
            { "status.psdLoadedMissing", ("PSD を読み込み: {0}（参照切れのテクスチャ {1} 件）", "PSD loaded: {0} ({1} missing texture(s))") },
            { "status.psdReadFailed", ("PSD を読み込めません", "Cannot read the PSD") },
            { "status.psdNotPsd", ("PSD ファイルではありません", "Not a PSD file") },
            { "status.psdNotDtx", ("DTXTool で出力した PSD ではないため読み込めません", "This PSD was not exported by DTXTool") },
            { "status.psdCorrupted", ("PSD の復元情報が破損しています", "The resume data in this PSD is corrupted") },
            { "status.psdTooNew", ("新しいバージョンの DTXTool で出力された PSD です。DTXTool を更新してください", "This PSD needs a newer version of DTXTool") },
            { "undo.addLayer", ("DTXTool: レイヤー追加", "DTXTool: Add Layer") },
            { "undo.duplicateLayer", ("DTXTool: レイヤー複製", "DTXTool: Duplicate Layer") },
            { "undo.deleteLayer", ("DTXTool: レイヤー削除", "DTXTool: Delete Layer") },
            { "undo.moveLayer", ("DTXTool: レイヤー順序", "DTXTool: Reorder Layer") },
            { "undo.visibility", ("DTXTool: 表示切替", "DTXTool: Toggle Visibility") },
            { "undo.rename", ("DTXTool: 名前変更", "DTXTool: Rename") },
            { "undo.blend", ("DTXTool: ブレンド", "DTXTool: Blend") },
            { "undo.opacity", ("DTXTool: 不透明度", "DTXTool: Opacity") },
            { "undo.mask", ("DTXTool: マスク", "DTXTool: Mask") },
            { "undo.maskImage", ("DTXTool: マスク画像", "DTXTool: Mask Image") },
            { "undo.fill", ("DTXTool: ベース", "DTXTool: Base") },
            { "undo.normal", ("DTXTool: ノーマル", "DTXTool: Normal") },
            { "undo.placement", ("DTXTool: 配置", "DTXTool: Placement") },
            { "undo.background", ("DTXTool: 下地", "DTXTool: Background") },
            { "undo.resolution", ("DTXTool: 解像度", "DTXTool: Resolution") },
            { "undo.beginSelect", ("DTXTool: 範囲選択", "DTXTool: Begin Selection") },
            { "undo.reselect", ("DTXTool: 範囲の再調整", "DTXTool: Adjust Area") },
            { "undo.loadPsd", ("DTXTool: PSD 読み込み", "DTXTool: Load PSD") },
            { "undo.applyOutput", ("DTXTool: 出力を適用", "DTXTool: Apply Output") },

            // ツールタブ
            { "window.title", ("DTXTool", "DTXTool") },
            { "button.start", ("作業開始", "Start") },
            { "button.end", ("終了", "End") },
            { "button.edit", ("編集 ▶", "Edit ▶") },
            { "button.export", ("出  力", "Export") },
            { "button.writeLayout", ("書き出し", "Write") },
            { "button.cancel", ("キャンセル", "Cancel") },
            { "target.placeholder", ("Renderer を選択", "Select a Renderer") },
            { "target.header", ("ターゲットメッシュ", "Target Mesh") },
            { "output.header", ("出力", "Output") },
            { "output.channel", ("チャンネル", "Channels") },
            { "output.details", ("詳細", "Details") },
            { "output.invert", ("反転", "Invert") },
            { "output.margin", ("余白", "Margin") },
            { "output.saveInverted", ("反転も保存", "Also save inverted") },
            { "output.maskSubfolder", ("Mask/ に保存", "Save in Mask/ folder") },
            { "output.basePng", ("Base PNG", "Base PNG") },
            { "uv.drop", ("取り込む", "Import") },
            { "uv.import", ("マスク取り込み", "Import mask") },
            { "uv.importHint", ("マスク画像をこのプレビューにドラッグ＆ドロップすると、黒い部分を塗った領域として取り込みます（今の塗りに追加。Ctrl+Z で戻せます）。",
                "Drag and drop a mask image onto this preview to import its black areas as painted (added to the current paint; Ctrl+Z to undo).") },
            { "uv.baseOverlay", ("ベーステクスチャを重ねる", "Overlay base texture") },
            { "layout.title", ("UV 展開図の書き出し", "Export UV Layout") },
            { "layout.size", ("解像度", "Size") },
            { "layout.elements", ("描画要素", "Elements") },
            { "layout.noSession", ("作業中の対象がありません", "No target in progress") },
            { "layout.wire", ("ポリゴン線", "Wireframe") },
            { "layout.seams", ("シーム線", "Seams") },
            { "layout.selectionOnly", ("選択限定", "Selection only") },
            { "layout.colorFill", ("アイランド着色", "Island coloring") },
            { "layout.background", ("背景", "Background") },
            { "layout.bgTransparent", ("透過", "Transparent") },
            { "layout.bgBase", ("ベース", "Base") },
            { "layout.seamColor", ("縁の色", "Seam color") },
            { "layout.wireColor", ("内部エッジの色", "Edge color") },
            { "layout.white", ("白", "White") },
            { "layout.black", ("黒", "Black") },
            { "layout.cyan", ("シアン", "Cyan") },
            { "layout.red", ("赤", "Red") },
            { "layout.fill", ("アイランド色", "Island color") },
            { "layout.islands", ("島ごと", "Islands") },
            { "summary.folder.texture", ("テクスチャと同じ場所", "Next to texture") },
            { "tooltip.settings", ("設定", "Settings") },
            { "tooltip.reload", ("更新を確認", "Check for updates") },
            { "tooltip.baseOverlay", ("ベーステクスチャを重ねる", "Overlay base texture") },
            { "tooltip.edit", ("この Renderer を DTXTool で編集する", "Edit this Renderer with DTXTool") },
            { "tooltip.layout", ("UV 展開図の書き出しを開く", "Open UV layout export") },
            { "tooltip.resetView", ("100% のサイズで元の位置に戻す（F）", "Reset to 100% and center (F)") },
            { "tooltip.overrideDot", ("既定に戻す", "Reset to default") },
            { "tooltip.manual", ("マニュアルを開く", "Open the manual") },
            { "tooltip.menu", ("メニュー", "Menu") },
            { "tooltip.openRelease", ("クリックで配布ページを開く", "Click to open the release page") },
            { "tooltip.frameTarget", ("クリックでシーンビューを対象に合わせる", "Click to frame the target in the Scene view") },
            { "tooltip.switchTarget", ("同じアバターの別の Renderer に切り替える", "Switch to another Renderer in the same avatar") },
            { "tooltip.candidate", ("クリックで選択、ダブルクリックで作業開始", "Click to choose, double-click to start") },
            { "tooltip.chooseTarget", ("対象の Renderer を選んでください", "Choose a target Renderer") },
            { "tooltip.submesh", ("サブメッシュ", "Submesh") },
            { "tooltip.uvChannel", ("UV チャンネル", "UV channel") },
            { "tooltip.baseAlpha", ("ベーステクスチャの不透明度", "Base texture opacity") },
            { "tooltip.toOutput", ("クリックで出力の設定へ", "Click to go to the output settings") },
            { "tooltip.sizeLocked", ("Base PNG の大きさで出力します", "Exported at the Base PNG size") },
            { "menu.preferences", ("環境設定（Preferences）", "Preferences") },
            { "menu.projectSettings", ("プロジェクト設定（Project Settings）", "Project Settings") },
            { "menu.checkUpdate", ("更新を確認", "Check for updates") },
            { "menu.openRelease", ("配布ページを開く", "Open the release page") },
            { "session.state", ("作業中", "Working") },
            { "session.hint", ("で終了", "to end") },
            { "session.end.user", ("作業を終了しました", "Work ended") },
            { "session.end.escape", ("Esc で作業を終了しました", "Ended with Esc") },
            { "session.end.sceneChanged", ("シーンが切り替わったため作業を終了しました", "Ended because the scene changed") },
            { "session.end.targetLost", ("対象が削除されたため作業を終了しました", "Ended because the target was deleted") },
            { "session.end.replaced", ("別のツールで作業を始めたため、こちらは終了しました", "Ended because work started in another tool") },
            { "info.triangles", ("三角形 {0}", "{0} triangles") },
            { "info.submeshes", ("サブメッシュ {0}", "{0} submeshes") },
            { "output.size", ("解像度", "Size") },
            { "uv.dropHint", ("マスク画像のドロップで取り込み", "Drop a mask image to import") },

            // ステータス・トースト
            { "status.saved", ("保存: {0}", "Saved: {0}") },
            { "status.updated", ("更新: {0}", "Updated: {0}") },
            { "status.applied", ("適用: {0}", "Applied: {0}") },
            { "status.layoutSaved", ("保存: {0}", "Saved: {0}") },
            { "status.restored", ("前回の作業を復元", "Restored previous work") },
            { "status.meshChanged", ("メッシュ変更のため復元なし", "Mesh changed; not restored") },
            { "status.uvFallback", ("UV{0} なし → UV0 を使用", "UV{0} missing; using UV0") },
            { "status.empty", ("選択なし（全白で出力）", "Nothing selected (exported white)") },
            { "status.nothingToImport", ("取り込める部分なし", "Nothing to import") },
            { "status.imported", ("取り込み: {0}", "Imported: {0}") },
            { "status.cleared", ("クリアしました", "Cleared") },
            { "status.writeFailed", ("書き込みできません", "Cannot write file") },
            { "status.version.checking", ("確認中…", "Checking…") },
            { "status.version.update", ("v{0} 更新あり", "v{0} available") },
            { "status.version.error", ("確認できません", "Check failed") },
            { "info.readWriteEnabled", ("Read/Write を有効化しました", "Enabled Read/Write") },
            { "info.ndmfMismatch", ("NDMF プレビューの頂点数が元のメッシュと異なるため、元のメッシュで作業中", "NDMF preview has a different vertex count; working on the original mesh") },
            // エラー
            { "error.noMesh", ("メッシュがありません", "No mesh") },
            { "error.notReadable", ("メッシュを読み込めません", "Mesh is not readable") },
            { "error.noUV", ("UV がありません", "Mesh has no UVs") },
            { "error.shaderMissing", ("シェーダーが見つかりません", "Shaders not found") },
            { "error.gpuUnsupported", ("この GPU では動作しません", "GPU not supported") },
            { "error.startFailed", ("開始できません: {0}", "Cannot start: {0}") },

            // Undo 名
            { "undo.select", ("UV Mask: 選択", "UV Mask: Select") },
            { "undo.paint", ("UV Mask: ペイント", "UV Mask: Paint") },
            { "undo.clear", ("UV Mask: クリア", "UV Mask: Clear") },
            { "undo.import", ("UV Mask: 取り込み", "UV Mask: Import") },
            { "undo.restore", ("UV Mask: 前回の作業を復元", "UV Mask: Restore") },
            { "undo.apply", ("UV Mask: マテリアルに適用", "UV Mask: Apply to material") },

            // ヒント
            { "hint.click", ("クリックで選択", "Click to select") },
            { "hint.symmetry", ("X で左右同時", "X: both sides") },
            { "hint.ctrlErase", ("Ctrl で消去", "Hold Ctrl to erase") },

            // 設定画面
            { "prefs.selectedColor", ("選択色", "Selection color") },
            { "prefs.maskAlpha", ("マスク表示の不透明度", "Mask opacity") },
            { "prefs.seamColor", ("シーム色", "Seam color") },
            { "prefs.seamThickness", ("シームの太さ", "Seam thickness") },
            { "prefs.xray", ("X-Ray を既定で ON", "X-Ray on by default") },
            { "prefs.isolate", ("孤立表示を既定で ON", "Isolate by default") },
            { "prefs.resetHints", ("ヒントをもう一度表示", "Show hints again") },
            { "prefs.shortcuts", ("ショートカットを編集", "Edit shortcuts") },
            { "project.defaultSize", ("既定の解像度", "Default size") },
            { "project.useTextureFolder", ("テクスチャと同じ場所に保存", "Save next to texture") },
            { "project.outputDir", ("保存先フォルダ", "Output folder") },
            { "project.maskSubfolder", ("Mask/ サブフォルダを作成して保存", "Create and save in Mask/ subfolder") },
            { "project.invert", ("反転", "Invert") },
            { "project.margin", ("余白（-1 = 自動）", "Margin (-1 = auto)") },
            { "project.saveInverted", ("反転も保存", "Also save inverted") },
            { "project.autoApply", ("出力後に自動で適用", "Apply after export") },
            { "project.threshold", ("取り込みのしきい値", "Import threshold") },
            { "project.mirrorTolerance", ("対称判定の許容誤差（0 = 自動）", "Symmetry tolerance (0 = auto)") },
            { "project.singleChannel", ("単一チャンネルで取り込む", "Import as single channel") },
        };

        public static bool Japanese
        {
            get
            {
                var lang = SettingsService.Get(SettingKeys.Language);
                if (lang == UILanguage.Japanese) return true;
                if (lang == UILanguage.English) return false;
                return Application.systemLanguage == SystemLanguage.Japanese;
            }
        }

        public static string T(string key)
        {
            if (!Table.TryGetValue(key, out var v)) return key;
            return Japanese ? v.ja : v.en;
        }

        public static string F(string key, params object[] args) => string.Format(T(key), args);

        /// <summary>フォントのアトラスを先に作っておくための全日本語文字列。</summary>
        public static string AllJapanese()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var v in Table.Values) sb.Append(v.ja);
            return sb.ToString();
        }
    }

    internal enum MessageLevel
    {
        Info,
        Success,
        Warning,
        Error,
    }

    internal enum MessageOrigin
    {
        Window,
        Scene,
    }

    /// <summary>ステータスバー / トーストに出す 1 行（UI 設計 §2.6.1 原則 4）。</summary>
    internal sealed class UserMessage
    {
        public MessageLevel Level;
        public string Text;
        public MessageOrigin Origin;
        public string AssetPath;          // クリックで Ping
        public string ActionLabel;        // トーストのボタン（[適用] など）
        public System.Action<UnityEngine.UIElements.VisualElement> Action;  // 引数はボタン（候補リストの表示位置）
    }

    /// <summary>メッセージの出口。表示はタブのステータスバーかシーンのトーストのどちらか 1 か所。</summary>
    internal static class Notifier
    {
        public static event System.Action<UserMessage> Posted;

        public static void Post(MessageLevel level, string text, MessageOrigin origin = MessageOrigin.Window)
            => Post(new UserMessage { Level = level, Text = text, Origin = origin });

        public static void Post(UserMessage message) => Posted?.Invoke(message);
    }
}
