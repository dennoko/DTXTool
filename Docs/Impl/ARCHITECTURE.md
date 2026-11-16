# DTXTool アーキテクチャ設計書

[requirements.md](requirements.md) を実現するための構造設計。「何を作るか」は要件定義書、「どう分けて、どうつなぐか」をこの文書で定める。
各節の見出しに対応する要件の節番号（例: `要件 §3 Step 1`）を付記する。内容は実装（`Editor/`・`Tests/`）と一致させてある。

---

## 1. 設計方針（主要な決定）

| # | 決定 | 理由 |
|---|------|------|
| D1 | **真実は 1 つのドキュメント（`DtxDocument`）**。RenderTexture・解析結果・UI はすべてそこから作り直せるキャッシュとして扱う | Undo / ドメインリロード / PSD 復元を「ドキュメントを差し替えて再評価する」だけで済ませるため |
| D2 | **レイヤーはパラメトリック**（合成結果のピクセルを持たない）。選択三角形・テクスチャ参照・数値だけを持ち、合成は毎回シェーダーで評価する | 解像度非依存（プレビューは低解像度、ベイクは 4096）、PSD の復元情報が小さく済む、再調整が常に無劣化 |
| D3 | **2 アセンブリ構成**: `Core`（CPU のみ・UnityEditor を使わない）← `Editor`（GPU・セッション・出力・UI）。UI は `Editor/UI/` にフォルダで分け、ロジック側から UI を呼ばない | 要件 §4 の「Core と UI の分離・テスト容易性」。取り込んだ MaskMaker V4 のセッション層が UI 部品（文言・通知）と同じアセンブリを前提にしているため、UI を別アセンブリに切り出すより、依存の向きを規約で守るほうが安全と判断した |
| D4 | **MaskMaker V4 の選択コアはソースを取り込み、名前空間を変えて同梱**する（アセンブリ参照はしない） | 要件 §3 Step 1「外部パッケージへの依存なく単体で完結」。MaskMaker V4 と同じプロジェクトに共存しても衝突させない |
| D5 | **プレビュー用とベイク用は同じ合成器**（`Compositor`）を解像度だけ変えて使う | 「プレビューと出力が違う」事故を構造的に防ぐ |
| D6 | **ブレンドはガンマ空間で計算**し、マテリアルへ渡す直前（Present パス）でプロジェクトの色空間に合わせる | 要件 §4。Photoshop 等と結果を揃え、PSD の見た目とも一致させる |
| D7 | **PSD の復元情報は Image Resource ブロック（プラグイン用 ID 域）に格納**。読み込みはその情報だけを使い、ピクセルレイヤーは読まない | レイヤー一覧に不可視のメタ用レイヤーを出さず「綺麗な構造」を保つ。読み込みが小さく堅牢になる（§9.3 にトレードオフ） |
| D8 | **マテリアルへの注入は「先に復旧ジャーナルを書いてから」行う** | クラッシュ時にも必ず元へ戻せる（要件 §2 プレビュー安全機構） |

---

## 2. 全体構成

```
┌──────────────────── Editor/UI（Dennoko.DTXTool.Editor の UI フォルダ）────────────────────┐
│  DTXToolWindow（UI Toolkit）   LayersCard / LayerInspector / TargetCard / UVCard            │
│  UVViewElement（2D UV）        CapsuleOverlay（シーンのカプセル）/ SceneHud / DtxRadialMenu  │
│  Input: SceneInput / Gestures / DtxShortcuts / DtxSelectTool（EditorTool）                  │
└───────────────┬──────────────────────────────────────────────┬───────────────────────────┘
                │ コマンド呼び出し                              │ 状態の購読（UiHub）
┌───────────────▼──────────── Editor（Dennoko.DTXTool.Editor）──▼───────────────────────────┐
│  Session    : SessionController / WorkSession / SessionLifecycle / DtxModeController       │
│  State      : DocumentHost（Undo 対象） / DocumentCommands / EditState（作業中の選択）/ Settings │
│  Gpu        : GpuResources / TriMesh / MaskPipeline / LayerMaskBuilder / Compositor / DtxRenderer │
│  Preview    : PreviewInjector / PreviewGuards / PreviewRecoveryJournal / ShaderPropertyResolver │
│  Export     : TextureBaker / PngExporter / PsdExporter / PsdImporter / DocumentAutoSave / OutputPaths │
└───────────────┬────────────────────────────────────────────────────────────────────────────┘
                │
┌───────────────▼──────────── Core（Dennoko.DTXTool.Core）──────────────────────────────────┐
│  Model         : DtxDocument / Layer / MaskSpec / UvTransform / TextureRef / DocumentOps    │
│  Analysis / Selection / Picking / Imaging : MaskMaker V4 から取り込んだ選択コア             │
│  Psd           : PsdWriter / PsdReader / PackBits                                           │
│  Serialization : DocumentCodec（PSD の復元情報・自動保存で共通のバイナリ形式）/ MaskRaster   │
│  Math          : BlendMath / NormalMath / UvMath（シェーダーの CPU 参照実装）                │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 2.1 アセンブリ

| asmdef | 参照 | UnityEditor API | 内容 |
|--------|------|-----------------|------|
| `Dennoko.DTXTool.Core` | なし | **使わない**（`UnityEngine` の数学型のみ） | モデル、選択、解析、PSD I/O、シリアライズ、計算式 |
| `Dennoko.DTXTool.Editor` | Core | 使う | GPU 合成、セッション、プレビュー、出力、UI |
| `Dennoko.DTXTool.Tests` | Core, Editor, TestRunner | 使う | EditMode テスト（`UNITY_INCLUDE_TESTS` 制約） |

- すべて `includePlatforms: ["Editor"]`。ビルドには一切含まれない（要件 §1）。
- `Editor` の内部型は `InternalsVisibleTo` で `Tests` にだけ公開する。

### 2.2 ディレクトリ構成

```
Assets/dennokoworks/DTXTool/
├─ Docs/Impl/                  requirements.md / ARCHITECTURE.md
├─ Editor/                     Dennoko.DTXTool.Editor.asmdef
│  ├─ Core/                    Dennoko.DTXTool.Core.asmdef
│  │  ├─ Model/                DtxDocument.cs（モデル一式）, DocumentOps.cs
│  │  ├─ Serialization/        DocumentCodec.cs（MaskRaster を含む）
│  │  ├─ Psd/                  PsdWriter.cs（PackBits を含む）, PsdReader.cs
│  │  ├─ Math/                 BlendMath.cs（NormalMath / UvMath を含む）
│  │  └─ Analysis/ Selection/ Picking/ Imaging/ Util/   （MaskMaker V4 より）
│  ├─ Session/                 SessionController, WorkSession, SessionLifecycle, SessionCommands,
│  │                           DtxModeController, SceneSessionBridge, WorkMesh/
│  ├─ State/                   DocumentHost, DocumentCommands, EditState, EditCommands, Settings/
│  ├─ Gpu/                     GpuResources, TriMesh, MaskPipeline, PaintSurface, PaintOps,
│  │                           LayerMaskBuilder, Compositor, DtxRenderer
│  ├─ Preview/                 PreviewInjector.cs（ShaderPropertyResolver / PreviewGuards / PreviewRecoveryJournal を含む）
│  ├─ Export/                  TextureBaker, PngExporter, PsdExporter（PsdImporter / DocumentAutoSave を含む）,
│  │                           OutputPaths, MaskImporter, SourceImage
│  ├─ Persistence/             Persistence.cs（StoragePaths / AtomicFile / SaveScheduler）, TargetKey.cs
│  ├─ Rendering/               SceneOverlay, UVViewRenderer
│  ├─ Input/                   SceneInput, Gestures, DtxShortcuts, DtxSelectTool
│  ├─ UI/
│  │  ├─ Common/               DennokoTheme.uss, DennokoBlocks.uss, DTXTool.uss, L10n, UiCommon, Dennoko 共通部品
│  │  ├─ Window/               DTXToolWindow.cs/.uxml, LayersCard, LayerInspector, TargetCard, UVCard, UVViewElement
│  │  ├─ Scene/                CapsuleOverlay.cs/.uxml, SceneHud, DtxRadialMenu, DennokoSceneSession ほか
│  │  └─ SettingsPages/        DtxSettingsPages（Preferences / Project Settings）
│  └─ Shaders/                 Compose.shader, LayerMask.shader, UVRaster.shader, ImageOps.shader,
│                              Overlay.shader, Line.shader, DtxCommon.hlsl
└─ Tests/Editor/               Dennoko.DTXTool.Tests.asmdef とテスト
```

### 2.3 他の dennokoworks ツールとの共存ルール

取り込んだコードが同一プロジェクト内の MaskMaker V4 等と衝突しないよう、グローバルな名前はすべて DTXTool 固有にしてある。

| 対象 | 命名 |
|------|------|
| 名前空間 | `Dennoko.DTXTool` / `Dennoko.DTXTool.Core` |
| シェーダー名 | `Hidden/DTXTool/*` |
| Overlay ID / EditorTool 名 | `dennokoworks.DTXTool.*` / `"DTXTool"` |
| ショートカット ID・タグ | `DTXTool/…` / `DTXTool.Working` |
| 保存先 | `UserSettings/DTXTool/`、`ProjectSettings/DTXTool.json` |
| メニュー | `dennokoworks/DTXTool` |
| アセットの GUID | 取り込んだファイルもすべて新しい GUID（UXML / USS は GUID で読むため、元ツールのものと取り違えない） |
| シーン編集セッションの所有キー | **`Dennokoworks.SceneSession.Owner` をそのまま共有**（別ツールの編集セッションと排他にするための共通の取り決め） |

---

## 3. ドキュメントモデル（要件 §2・§3 Step 2）

`Core/Model` に置く `[Serializable]` のプレーンなクラス群。Unity オブジェクトへの直接参照は持たず、アセットは `TextureRef`（GUID + パスのヒント）で指す。Unity のシリアライズ（Undo）と `DocumentCodec` の両方で保存できる。

```
DtxDocument
├─ formatVersion
├─ width, height                       出力解像度（512 / 1024 / 2048 / 4096。常に正方形）
├─ uvChannel
├─ meshHash, triangleCount, submeshIndex, targetName   対象メッシュ（選択三角形が有効かの判定）
├─ background : Background
│    ├─ baseKind   : Color | Texture     + color / baseTexture
│    └─ normalKind : Flat  | Texture     + normalTexture
└─ layers : List<Layer>                添字 0 が最下層

Layer
├─ id（GUID。並べ替えても不変）, name, visible, opacity, blendMode（Normal / Multiply / Additive / Screen / Overlay）
├─ mask : MaskSpec
│    ├─ source       : Selection | Image | Embedded
│    ├─ selectedBits : int[]           Selection: 三角形ビット列
│    ├─ paintRle     : byte[]          Selection: 手塗り（4096² の RLE。空なら手塗りなし）
│    ├─ image        : TextureRef      Image: マスク画像
│    ├─ embedded, embeddedSize         0/255 のラスター（Deflate 圧縮）。Embedded の時はマスクそのもの。
│    │                                 Selection の時は PSD へ書く時だけ入る控え（§9.4）
│    └─ invert, opacity, feather       feather はテクスチャ幅に対する比率（解像度非依存）
├─ color, baseTexture                  ベースは「テクスチャ × 色」（テクスチャが無ければ色だけ）
├─ normalTexture, normalScale, replaceNormal
├─ normalMask                          ノーマルだけに掛けるマスク画像（空なら掛けない）
└─ placementMode : Tiling | Decal
     ├─ baseXform（tiling, offset, rotation）, syncNormal（既定 false）, normalXform
     └─ decalCenter, decalSize, decalRotation
```

- マスクの **反転・不透明度・ぼかし** は、マスクの作り方（選択 / 画像）に関係なく共通で効く。
- ノーマルは「テクスチャの有無 × `replaceNormal`（下地の凹凸を消す）」で決まる（§6.3）。どちらも無いレイヤーはノーマルに関わらない。
- `syncNormal` はベースのテクスチャがある時だけ効く（無ければノーマルは常に `normalXform`）。
- **旧形式（v0.1: `fillKind` / `tint` / `normalKind` で種類を選んでいた）からの移行**: `DocumentOps.MigrateLegacy` が今の形へ直す。
  呼ぶのは `DocumentCodec` の読み込み（末尾に `replaceNormal` が無いレイヤー）、`DocumentHost` の `OnEnable`・Undo / Redo・`SetDocument`
  （メモリ上のドキュメントや更新前の Undo 履歴は、`[FormerlySerializedAs]` で旧フィールドの値を `legacy*` に受ける）。
  書き込みは旧形式の並びも保ったまま末尾に `replaceNormal`、`normalMask` の順で足すので、古い DTXTool でもおおむね同じ見た目で読める。
- ドキュメントの書き換え（追加・複製・削除・並べ替え・メッシュへの適合）は `DocumentOps` にあり、Undo を知らない純粋な操作としてテストしている。

---

## 4. 状態とライフサイクル（要件 §2・§3 Step 3）

### 4.1 状態の置き場所

| 状態 | 置き場所 | Undo | リロード後 | 再起動後 |
|------|----------|:----:|:----------:|:--------:|
| ドキュメント本体・選択中のレイヤー | `DocumentHost`（`ScriptableSingleton`、`[FilePath]` なし） | ○ | 残る | 自動保存から復元 |
| 作業中の選択（これからレイヤーにする範囲の下書き） | `EditState`（同上。MaskMaker V4 のものをそのまま使う） | ○ | 残る | 破棄 |
| 編集モード・ツール設定（粒度・対称など） | `SettingsService` の Session 層 | × | 残る | 破棄 |
| 対象ごとの設定（シェーダープロパティ・出力名）・プロジェクト設定・個人設定 | `SettingsService` の Target / Project / User 層 | × | 残る | 残る |
| 解析結果・GPU リソース・RT | `WorkSession` | × | **作り直す** | 作り直す |
| プレビュー注入のバックアップ | `PreviewInjector` + 復旧ジャーナル | × | 作り直す | ジャーナルから復旧 |

### 4.2 変更の経路（一方向）

```
UI 操作 ─▶ DocumentCommands.X(...)          唯一の書き込み口
              │  Undo.RecordObject(DocumentHost, "…")
              │  DocumentOps / ラムダでドキュメントを書き換え
              ▼
          DocumentHost.Touch()               Revision++ と Changed の発行（Undo / Redo でも発行）
              ├─▶ DtxRenderer      次の update で入力のキーを比べ、変わっていれば再合成
              ├─▶ DocumentAutoSave 2 秒のデバウンスで保存予約
              └─▶ UiHub            全ビューが今の状態から表示を作り直す（同じフレームの変更は 1 回にまとめる）
```

- UI はドキュメントを直接書き換えない。表示は常にドキュメントから `SetValueWithoutNotify` で作る（`DocBind`）。
- 「何が変わったか」は通知せず、`Revision` だけを上げる。合成側はレイヤーごとのマスクの内容キーを比べて、必要なものだけ作り直す（§6.5）。

### 4.3 セッション

`SessionController`（開始・終了の唯一の入口）と `WorkSession`（1 回の作業が所有するキャッシュ一式）は MaskMaker V4 の構造を踏襲する。

```
Start(renderer)
  1. TargetKey を求め、ターゲット別設定を読み込む
  2. WorkMesh を作る（SkinnedMeshRenderer はポーズ追従のベイク）
  3. MeshAnalyzer.Analyze → Raycaster / UVGrid を作る
  4. ドキュメントを決める：メモリ上が同じ対象ならそのまま ／ 自動保存があれば復元 ／ なければ新規
     メッシュが変わっていたら DocumentOps.AdaptToMesh（選択で作ったレイヤーの範囲は空になる）
  5. GPU リソースを作る（TriMesh / MaskPipeline / DtxRenderer）
  6. 1 回合成してから PreviewInjector.Begin（ジャーナル書き込み → RT 注入）
  7. 新規ドキュメントなら、マテリアルに元から入っていた BaseMap / NormalMap を下地にし、解像度も合わせる
  ※ 途中で失敗したら StartTransaction が逆順に巻き戻す
End(reason)
  PreviewInjector.End（元へ復元・ジャーナル削除）→ 自動保存を Flush → WorkSession.Dispose
```

`SessionLifecycle` が Unity のイベントをまとめて購読する。

| イベント | 動作 |
|----------|------|
| `beforeAssemblyReload` / Play モード移行（`ExitingEditMode`） | 再開トークン（対象の `GlobalObjectId`）を `SessionState` に残して End → 戻ったら自動で Start し直す |
| シーン切替・Prefab Stage 開閉・対象の削除 | 再開せずに End |
| `EditorApplication.quitting` | Flush して End |
| ウィンドウを閉じる | End（要件 §2） |

### 4.4 編集モード（`DtxModeController`）

| モード | シーン入力 | 内容 |
|--------|:----------:|------|
| `LayerEdit`（既定） | 奪わない | レイヤーの質感・配置・順序を調整する。シーンには何も重ねない（質感そのものがプレビューで出ている）。UV ビューは出さない（デカールの位置・大きさ・回転は質感カードの数値で決める） |
| `SelectNew` | 奪う | `EditState` を空にして範囲を選ぶ → **確定**（Enter）で新規レイヤーを作って `LayerEdit` へ。空のままでは確定できない |
| `Reselect(layerId)` | 奪う | レイヤーの範囲を `EditState` に読み込んで直す。**確定するまでドキュメントは書き換えない**が、合成は作業中の選択のマスクを直接使うので、モデル上の見た目はその場で変わる。**取消**（Esc）で元の範囲に戻る |

- 選択用のショートカット（粒度・対称・全選択など）とラジアルメニューは、範囲を選んでいる間だけ有効にする。
- Esc は、範囲を選んでいる間は「選択の取消」、レイヤーの調整中は「作業の終了」。

---

## 5. 選択とマスク（要件 §3 Step 1）

### 5.1 MaskMaker V4 から取り込んだもの

| 取り込み元（`MaskMakerV4/Editor/`） | 変更点 |
|------|------|
| `Core/*`（解析・選択・ピッキング・画像処理） | 名前空間のみ |
| `Session/*`, `State/*`, `Persistence/*` | ドキュメントの復元・プレビュー注入・編集モードを追加。選択の自動保存（session.bin）は廃止し、ドキュメントの自動保存に置き換え |
| `Gpu/*`, `Rendering/*`, `Shaders/*` | 作業中の選択用のパイプラインはそのまま。レイヤー用の `LayerMaskBuilder` / `Compositor` / `DtxRenderer` と `Compose.shader` / `LayerMask.shader` を追加 |
| `Input/*`, `UI/Scene/*`, `UI/Common/*` | 範囲を選んでいる間だけ働くようにし、カプセルに「範囲選択を始める」「確定 / 取消」を追加 |
| `UI/Window/TargetCard`, `UVCard`, `UVViewElement` | UV ビューは合成結果を下に表示。デカールの配置操作と、テクスチャのドロップでマスク画像レイヤーを作る処理を追加 |

取り込まなかったもの: マスク PNG の出力（`MaskExporter` / `OutputCard`）、UV 展開図の書き出し、マテリアルへのマスク適用、バージョン確認、NDMF 連携。

クリック選択の機能（UV アイランド単位 / 連結単位 / ポリゴン単位、対称ミラー、Shift 追加・Ctrl 除外、ドラッグ・Shift+ホイールでの拡縮）は取り込んだ `SelectionOps` と `Gestures` で満たしている。**手塗り（ブラシ・矩形・投げ縄・消しゴム）も残してある**（選択の仕組みと一体になっており、外すより残すほうが安全で、範囲指定の手段としても有用なため）。

### 5.2 マスクの生成経路（`LayerMaskBuilder`）

```
Selection ─▶ 三角形フラグ更新 ─▶ UVRaster（UV 空間へ描く）＋ 手塗りの合流 ─▶ 余白の拡張 ─┐
Image     ─▶ 輝度 × アルファ（sRGB 取り込みなら生の値へ戻す）─────────────────────────────┤
Embedded  ─▶ 展開して R8 テクスチャ化 ───────────────────────────────────────────────────┤
                                                                                          ▼
                                                      ぼかし（分離ガウス H/V）─▶ 反転 ─▶ × 不透明度 ─▶ マスク RT
```

- 選択由来のマスクには、UV シームで下地が覗かないよう余白の拡張を必ずかける（面が使っていない画素へだけ広げる）。ぼかす時は、ぼかす幅の分だけ余白を広げておく（シームで縁が欠けない）。
- ぼかしは UV の端（0 / 1）で反対側へ回り込ませない。
- 作業中の選択用の `MaskPipeline` とは別の RenderTexture を使う。三角形フラグのバッファだけは共有するので、使い終わったら作業中の選択の内容へ戻す。

---

## 6. 合成パイプライン（要件 §3 Step 2・§4）

### 6.1 全体の流れ

`Compositor` は解像度 `R` を受け取り、ドキュメントを下から順に評価する。プレビューもベイクも同じクラス・同じシェーダーを通る（D5）。マスクとテクスチャの出どころだけを `ICompositeSource` で差し替える（プレビュー = キャッシュする `DtxRenderer`、ベイク = 使い終わったら解放する `BakeSource`）。

```
                       ┌──────────── Base 系列（ARGB32・ガンマ値を保持）───────────┐
Background.base ─▶ InitBase ─▶ BlendBase(L0) ─▶ BlendBase(L1) ─▶ … ─▶ PresentBase ─▶ BaseMap RT
                                   ▲ mask(L0)        ▲ mask(L1)
Background.normal ▶ InitNormal ▶ BlendNormal(L0) ▶ BlendNormal(L1) ▶ … ▶ PresentNormal ▶ NormalMap RT
                       └──────────── Normal 系列（ARGBHalf・ベクトルを保持）──────┘
```

- 累積はピンポン（A ⇄ B の 2 枚）。レイヤーの中身（単色 / テクスチャ / タイリング / デカール）は `BlendBase` / `BlendNormal` のフラグメントシェーダー内で直接評価する。
- 非表示・不透明度 0・テクスチャ指定なのに参照切れ、のレイヤーはパスごとスキップする。

### 6.2 Base の合成と色空間

```
src   = fill が Color なら color、Texture なら sample(tex, layerUV) × tint      （ガンマ値）
a     = mask × layer.opacity × src.a （デカールは範囲外で 0）
out.rgb = lerp(dst.rgb, Blend(dst.rgb, src.rgb, blendMode), a)
out.a   = dst.a                                                                （下地のアルファを保つ）
```

| 段階 | Linear プロジェクト | Gamma プロジェクト |
|------|--------------------|--------------------|
| sRGB テクスチャのサンプリング | 得られるのはリニア値 → シェーダーで**ガンマ値へ戻して**から使う | そのまま使う |
| 色（単色・Tint） | ガンマ値のまま `SetVector` で渡す（`SetColor` はリニアへ変換するので使わない） | 同左 |
| 累積 RT | `RenderTextureReadWrite.Linear` の ARGB32 に**ガンマ値を保存** | 同左 |
| Present（マテリアルへ渡す RT） | sRGB の RT へ、ガンマ値 → リニア値に戻して書く（書き込み時に sRGB エンコードされ、元の値がそのまま入る） | そのままコピー |
| PNG 書き出し | 累積 RT の値をそのまま読み戻して保存 | 同左 |

ブレンド式は `Core/Math/BlendMath` に CPU 実装を持ち、5 つのブレンドモードすべてでシェーダーと一致することをテストしている。

### 6.3 Normal の合成

```
n_layer = Decode(tex, layerUV)          Normal map でインポート済みなら RG / AG 格納、そうでなければ rgb × 2 − 1
n_layer.xy *= normalScale
n_layer.xy  = Rotate(n_layer.xy, 絵が表面上で回って見える角度)   UV を回したら法線の向きも回す
n_layer     = normalize(n_layer)

テクスチャあり・replaceNormal = false（重ねる）  : out = normalize(lerp(n_dst, RNM(n_dst, n_layer), a))
テクスチャあり・replaceNormal = true （置き換え）: out = normalize(lerp(n_dst, n_layer,             a))
テクスチャなし・replaceNormal = true （平坦化）  : out = normalize(lerp(n_dst, (0,0,1),             a))
テクスチャなし・replaceNormal = false            : このレイヤーの Normal パスを実行しない

a = mask × opacity × Inside(layerUV) × normalMask(uv)    normalMask は輝度 × アルファ（指定が無ければ 1）
```

- **ノーマル用マスク**（`normalMask`）はノーマルのパスにだけ掛かり、ベースには効かない。マスク画像（§5）と同じく UV 全体に対応し、
  レイヤーのマスクに掛け合わせる。参照が切れている時は、そのレイヤーのノーマルを描かない（マスク画像の参照切れと同じ扱い）。
  PSD では NormalMap フォルダのレイヤーのアルファに入れる（レイヤーマスクはベースと共通のまま）。

- 既定の合成は **RNM（Reoriented Normal Mapping）＋正規化**。ブレンドモードは Base にだけ効く。
- RNM は下地の凹凸を残す（ディテールを足す用途向き）。別の素材を貼るように下の凹凸を消したい時は「下地の凹凸を消す」で**置き換えの補間**にする。
  平坦化は「置き換え先が平坦な法線」の場合で、同じ式になる（RNM で平坦な法線を重ねても下が残るため、RNM では表現できない）。
- シェーダーの `_NormalMode` は 1 = 平坦化 / 2 = RNM / 3 = 置き換え。
- 累積は ARGBHalf に生のベクトルで持ち、8bit への量子化は最後の 1 回だけ。
- **PresentNormal** は `(n × 0.5 + 0.5, 1)` をリニアの RT に書く。アルファが 1 なので、Standard / URP Lit / lilToon の `UnpackNormal` でそのまま読める。

### 6.4 配置（タイリング・デカール）

```
Tiling : uv' = Rot(rotation) · ((uv − 0.5) × tiling) + 0.5 + offset      frac で折り返す（Clamp のテクスチャでも繰り返す）
Decal  : uv' = Rot(−rotation) · (uv − center) / size + 0.5               [0,1] の外は寄与 0
```

どちらも一次変換 `uv' = A·uv + T` なので、C# 側（`UvMath.Affine`）で A と T を作ってシェーダーへ渡す。`syncNormal = true` のときは Normal も `baseXform` を使う。

### 6.5 リアルタイム性の確保（`DtxRenderer`）

- **合成は `EditorApplication.update` で 1 フレームに最大 1 回**。変更通知を受けた時点では何もせず、update で入力のキー（ドキュメントの Revision・解像度・モード・作業中の選択の版・外部テクスチャの変更）を比べ、変わっていた時だけ合成する。
- **マスクのキャッシュ**: レイヤーごとの仕上げ済みマスクを、内容のキー（選択・手塗り・画像・反転・不透明度・ぼかし・解像度）で保持する。質感や配置のスライダーを動かしてもマスクは作り直さない。
- **プレビュー解像度**は Preferences で出力解像度と独立に選べる（既定 2048、出力解像度が上限）。ベイク時だけ出力解像度で合成する。
- 参照しているテクスチャが再インポートされたら、次の update で作り直す。

---

## 7. プレビュー注入と安全機構（要件 §2）

### 7.1 シェーダープロパティの判別（`ShaderPropertyResolver`）

1. 既知の名前を順に探す（Base: `_MainTex` → `_BaseMap` → `_BaseColorMap` → … ／ Normal: `_BumpMap` → `_NormalMap` → …）。
2. 無ければプロパティのフラグを見る（`[MainTexture]` → Base、`[Normal]` → Normal）。
3. 対象カードのドロップダウンに 2D テクスチャの全プロパティを出し、差し替えた結果はターゲット別設定に保存する。

ノーマルマップ未使用のマテリアルでは RT を入れても表示されないため、プレビュー中だけ有効化する（lilToon: `_UseBumpMap` = 1、Standard / URP Lit: `_NORMALMAP` キーワード）。これもバックアップ・復元の対象。

### 7.2 注入と復元（`PreviewInjector`）

```
Begin
  1. バックアップを作る：{マテリアル, プロパティ名, 元テクスチャ, 元のキーワード / _UseBumpMap}
  2. 復旧ジャーナルを書く（UserSettings/DTXTool/preview_recovery.json、一時ファイル → 置き換え）   ← 注入より先（D8）
  3. マテリアルへ RT をセット
Suspend / Resume   一時的に元へ戻す（保存・ベイクの間）／ 注入し直す
End                元の値をセットし、ジャーナルを削除
```

- 注入・復元は Undo に記録しない。
- 出力をマテリアルへ割り当てる時は、プレビューを外した状態で Undo に記録し、「元の値」を出力したテクスチャに差し替える（終了時にそれへ戻る）。

### 7.3 復元の契機

| 契機 | 検出 | 動作 |
|------|------|------|
| Ctrl+S・アセット保存 | `AssetModificationProcessor.OnWillSaveAssets` | **一時復元** → 保存 → 次の `update` で再注入 |
| シーン保存 | `EditorSceneManager.sceneSaving` | 同上 |
| Play モード移行 / スクリプト再コンパイル | `playModeStateChanged` / `beforeAssemblyReload` | セッション終了（戻ったら自動再開） |
| ウィンドウ閉鎖 / Unity 終了 | `OnDestroy` / `EditorApplication.quitting` | セッション終了 |
| クラッシュ・強制終了 | 次回起動時にジャーナルが残っている | `[InitializeOnLoadMethod]` → `delayCall` で、プレビューが動いていなければジャーナルから元テクスチャ・キーワードを復元して保存し、警告を出す |

ジャーナルは `GlobalObjectId` でアセットを指す。アセットでないマテリアル（シーン内インスタンス）は再起動後に辿れず、RT 参照も自然に消えるため対象外。

---

## 8. PNG 出力（要件 §3 Step 4-1）

```
TextureBaker.Bake(session, document)
  1. Compositor を出力解像度で実行（マスクも同解像度で作り、使い終わったらすぐ解放）
  2. エッジ拡張：対象サブメッシュの UV カバレッジを描き、面が使っていない画素へ隣の色を 1 画素ずつ押し広げる
       Base / Normal の両方に適用。幅は既定 = 解像度 / 128 px（Project Settings で変更可）。アルファは広げない
  3. Normal は拡張後に正規化して 8bit エンコード
  4. 読み戻し
PngExporter
  5. PNG エンコード → 一時ファイル経由で書き込み → インポート
       初回のインポートだけ設定を当てる（BaseMap: sRGB、NormalMap: Texture Type = Normal map）
  6. ステータスバーの［適用］で、出力したテクスチャを対象マテリアルへ割り当て（Undo に記録する本物の変更）
```

- ベイクと書き込みの間はプレビュー注入を一時復元しておく。
- 出力先は「元のテクスチャと同じフォルダ」（無ければ `Assets/DTXTool_Output`）、名前は `<対象名>_BaseMap.png` / `_NormalMap.png`。同じ名前へ上書きするので、割り当て済みのマテリアルは出力し直すだけで更新される。
- **ドキュメントが読んでいるテクスチャ（下地など）と同じパスには書かない**（`_2` を付けて避ける）。自分の入力を上書きすると、出力するたびにレイヤーが重なっていくため。
- **上書き**（フッターのチェックボックス、`SettingKeys.OverwriteOutput`、プロジェクト設定、既定オフ）。オフの時は BaseMap / NormalMap のどちらかが既にあれば、
  両方の拡張子の前に「 1」「 2」… と**同じ番号**を付け、どちらも空いている番号にする（`OutputPaths.PngPaths`）。オンの時は同じ名前に書く（上の `_2` の回避だけは効く）。

---

## 9. PSD ラウンドトリップ（要件 §3 Step 4-2）

### 9.1 書き出す PSD の構造

8bit RGB / RLE（PackBits）圧縮 / PSD バージョン 1。レイヤー名は Pascal 文字列に加えて Unicode 名（`luni`）を書き、日本語名を各ソフトで正しく表示させる。

```
<ファイル名>.psd
├─ 📁 NormalMap        （グループ・非表示・閉じた状態）
│    ├─ <ノーマルを持つレイヤー>
│    └─ Background
└─ 📁 BaseMap          （グループ・表示）
     ├─ <レイヤー名 N>      ← 最上層
     ├─ …
     └─ Background
```

| 項目 | BaseMap フォルダ | NormalMap フォルダ |
|------|------------------|--------------------|
| レイヤーのピクセル | 質感を全面にラスタライズ（タイリング・デカール適用済み、ストレートアルファ） | そのレイヤーの法線を全面にラスタライズ（強さ・回転適用済み） |
| レイヤーマスク | 最終マスク（ぼかし・反転・マスク不透明度適用済み） | 同左 |
| 不透明度・表示 | レイヤーの値 | 同左 |
| ブレンドモード | `norm` / `mul ` / `lddg` / `scrn` / `over` | 常に `norm` |
| ノーマルを持たないレイヤー | — | 出力しない |

- 統合画像（Merged Image）には BaseMap の合成結果（PNG 出力と同じもの）を書く。
- BaseMap フォルダは DTXTool の合成と同じ式・同じガンマ空間なので、外部ソフトで開いた見た目が一致する。
- **NormalMap フォルダは近似**。PSD のブレンドモードでは RNM を表現できないため、外部ソフト上では「上のレイヤーで置き換える」見え方になる。正確な NormalMap は PNG 出力を使う。

### 9.2 役割分担

| クラス | アセンブリ | 役割 |
|--------|-----------|------|
| `PsdWriter` | Core | バイナリを書く。レイヤーは下から上の順に足し、足した時点で圧縮して一時領域に貯める（非圧縮の全レイヤーをメモリに持たない） |
| `PsdReader` | Core | ヘッダーの確認と Image Resource の取り出し（ツールが使うのはここまで）。レイヤーの読み取りは、書き出した PSD の検証用 |
| `DocumentCodec` | Core | `DtxDocument` ⇄ バイト列 |
| `PsdExporter` | Editor | `Compositor` に各レイヤーをラスタライズさせて `PsdWriter` に渡す |
| `PsdImporter` | Editor | 復元情報を取り出し、検証してドキュメントを差し替える |

### 9.3 復元情報の格納（D7）

**Image Resource ブロック（`8BIM`、ID 4090 = プラグイン用の 4000–4999 の範囲、名前 `DTXTool`）** に `DocumentCodec` のバイト列を 1 つ書く。

```
"DTXM" | u16 形式バージョン | u32 本体の長さ | 本体（Deflate 圧縮） | u64 本体の FNV-1a
本体: ドキュメント / 下地 / 各レイヤーを、長さ付きのチャンクとして並べたもの
      （後のバージョンが末尾に項目を足しても、古い読み手が読み飛ばせる）
```

- `.json` などのサイドカーは作らない。PSD 単体で完結する。
- 同じ形式を自動保存（`UserSettings/DTXTool/targets/<key>/document.bin`）でも使う。こちらは編集中の保険であって PSD の付属物ではない。

**代替案（不可視レイヤー `_DTX_META` にピクセルとして埋め込む）を採らない理由と、受け入れるトレードオフ**

| | Image Resource（採用） | `_DTX_META` レイヤー |
|---|---|---|
| 配布 PSD の見た目 | レイヤー一覧に何も増えない | 用途不明のレイヤーが 1 枚見える |
| 読み込みの規模 | ファイルの先頭（ヘッダーとリソース節）だけ読めばよい | レイヤー情報・チャンネル展開まで必要 |
| 外部ソフトで上書き保存した後 | **ソフトによってはブロックが捨てられ、復元できなくなる** | ピクセルが無傷なら残る（ただし誤って編集・統合されると壊れる） |

外部ソフトで再保存した PSD は、そもそもピクセルの変更をパラメトリックなレイヤーへ取り込めないため「続きの調整」の対象にしない。再開用には DTXTool が書き出した PSD を残す運用とし、出力カードにその旨を表示している。

### 9.4 読み込みの手順と中断条件

```
PsdImporter.Import(path)
  1. ヘッダーを確認（"8BPS" / version 1）                    × → 「PSD ファイルではありません」
  2. Image Resource から DTXTool のブロックを探す             × → 「DTXTool で出力した PSD ではないため読み込めません」
  3. magic・チェックサムを検証                                × → 「PSD の復元情報が破損しています」
  4. 形式バージョンを確認                                     新しすぎる → 「新しいバージョンの DTXTool で出力された PSD です」
  5. メッシュの一致を確認（meshHash / triangleCount）
       一致   → 選択三角形をそのまま復元（範囲の再調整も可能）。控えのラスターは捨てる
       不一致 → 該当レイヤーのマスクを Embedded（控えのラスター）に切り替え、警告を表示
  6. 参照切れのテクスチャを数える                             あれば警告（読み込みは続行）
  7. 1 つの Undo 操作としてドキュメントを差し替え
```

- 中断はすべて「現在のドキュメントに一切手を付けない」状態で行う（検証が全部通ってから差し替える）。
- ピクセルレイヤーは読まない。見た目は復元したパラメータから再合成される。

---

## 10. UI（要件 §3 Step 3）

デザインは dennokoworks のデザインスキル（`.claude/skills/dennokoworks_color_schema`）に従う。画面パターンは **P4（シーン作業）**。

### 10.1 エディターウィンドウ（`DTXToolWindow`）

UXML で骨組みを定義し、各カードのクラスは「ドキュメントの一部を表示し、操作を `DocumentCommands` に渡す」だけにしてある。色は USS 変数だけを使う（例外は、レイヤーの見本のようにデータそのものが色である箇所）。

```
┌ トップバー（ツール名・バージョン・言語・設定メニュー）─────────────────────┐
│ B7 作業中バナー（作業中のみ。対象名・Esc の案内・終了）                       │
│ B3 UV ビュー       範囲を選んでいる間だけ。スクロール領域の外に固定           │
├─ スクロール領域 ─────────────────────────────────────────────────────────┤
│ B1 対象カード      対象 / サブメッシュ / UV                                   │
│                    ▶ 詳細: BaseMap・NormalMap のプロパティ、メッシュの情報     │
│ 範囲選択の案内     範囲を選んでいる間だけ（何を選んでいるか + キーの案内）    │
│ レイヤー一覧       上が最上段、最下段は下地。行の左の目で表示切替            │
│                    見出し行: ＋（範囲を選んで追加）・複製・上へ・下へ・削除   │
│                    一覧の下: 選択中レイヤーの名前・ブレンド・不透明度          │
│ マスク             ［範囲を再調整］（大きいボタン）・マスク画像               │
│                    ▶ 詳細設定: 反転・不透明度・ぼかし                         │
│ 質感               色 / テクスチャ（→ すぐ下に配置）/ ノーマル（→ すぐ下に強さと配置）│
│                    □ 下地の凹凸を消す / ノーマルのマスク                      │
│ 下地               一覧で下地を選んでいる時だけ                               │
│ PSD                PSD 保存・PSD 読み込み                                     │
├─ 出力ドック ─────────────────────────────────────────────────────────────┤
│ 解像度 / ファイル名（保存先と実際のファイル名はツールチップ）                 │
├─ フッター ───────────────────────────────────────────────────────────────┤
│ 主ボタンは常に 1 つ: 作業開始（Idle）／ 選択を確定（範囲選択中）／ PNG 出力    │
│ ステータスバー（結果と、その場でできる次の操作 1 つ。例: ［適用］）            │
└───────────────────────────────────────────────────────────────────────────┘
```

- 作業中にスクロールする量を減らすため、触る頻度の低いもの（シェーダープロパティ・メッシュの情報・マスクの細かい設定）は「▶ 詳細」に畳む。
  閉じている間は、見出し行に要約チップ（三角形数 / 初期値から変えたマスクの設定）を出す。開閉はエディターを閉じるまで `SessionState` に覚える。
- 色とテクスチャの欄は常に出し、両方効く（テクスチャ × 色）。テクスチャを初めて入れた時は、テクスチャの色をそのまま見せるため色を白にする（同じ Undo の中）。
- 配置はテクスチャ欄のすぐ下に、そのテクスチャを入れた時だけ出す。方式（タイリング / デカール）とデカールの位置はレイヤーに 1 つで、
  ベースのテクスチャがある時はベース側、無い時はノーマル側に出す。「ノーマルも同じ配置にする」（既定オフ）はノーマルの配置欄の下。
- UV ビューは範囲を選んでいる間だけ出す。ビュー自体のホイール・ドラッグとスクロールが重ならないよう、スクロール領域の外に固定する。
- 使わない欄は隠す（グレーアウトにしない）。範囲を選んでいる間は出力ドックと PSD カードを隠し、今やることだけを見せる。
- テクスチャをレイヤー一覧や UV ビューへドロップすると、マスク画像のレイヤーを作る。各テクスチャ欄へのドロップは差し替え。
- レイヤーの行をダブルクリックすると範囲の再調整に入る。

### 10.2 シーンビュー

- **カプセル（シーンツールバー）**: レイヤーの調整中は「範囲を選んで追加」「選択中レイヤーの再調整」だけ。範囲を選んでいる間は、選択 / ペイントの切替・選択の単位・対称・X-Ray・孤立表示・一時停止・Undo / Redo / クリア・**確定 / 取消**。
- **ラジアルメニュー**（C 長押し）: 選択の単位、対称、全選択・反転・クリア、モード切替。
- **ハイライト**: 範囲を選んでいる間だけ、選択範囲とシーム・ホバーの縁取りを重ねる。

---

## 11. Unity バージョン・レンダーパイプライン互換（要件 §1）

- **シェーダーはレンダーパイプラインに依存させない**。全画面パス・UV ラスターとも `LightMode` タグなしの素のパスで、`Graphics.Blit` / `Graphics.DrawMeshNow` で直接描く。
- UI は 2022.3 に存在する UI Toolkit の機能だけで組んである。
- シーンビューへの重ね描きは、MaskMaker V4 の `SceneOverlay` の方式をそのまま使う。
- **自動テストで確認済みなのは Unity 2022.3.22f1 × Built-in RP（Linear / Gamma）**。Unity 6・URP・lilToon での表示は自動テストの対象外で、エディター上での確認が必要（§13）。

---

## 12. テスト（要件 §4）

`Tests/Editor/` の EditMode テスト（Test Runner の EditMode タブ、または `-runTests -testPlatform EditMode`）。

| ファイル | 対象 | 内容 |
|----------|------|------|
| `CoreDocumentTests` | `DocumentCodec` / `DocumentOps` / `MaskRaster` | 全フィールドの往復、破損・他形式・新しすぎる形式の拒否、追加・複製・削除・並べ替え、メッシュが変わった時の扱い |
| `CoreMathTests` | `BlendMath` / `NormalMath` / `UvMath` | ブレンドの既知の値、sRGB の往復、RNM の恒等性、平坦化、UV 変形と一次変換の一致 |
| `CorePsdTests` | `PsdWriter` / `PsdReader` / `PackBits` | ヘッダー、グループ・名前（日本語）・ブレンド・不透明度・表示、画素とマスクの往復、上下の向き、復元情報の往復、PSD でないもの・復元情報の無い PSD・途中で切れた PSD の拒否 |
| `ShaderTests` | 同梱シェーダー 6 本 | 見つかる・コンパイルエラーが無い・パス数 |
| `GpuCompositorTests` | `Compose.shader` / `Compositor` / `TextureBaker.Pad` | 5 つのブレンドモード・テクスチャ（sRGB / リニア）・Tint・アルファ・タイリング / 回転・デカール・RNM・強さと回転・平坦化・Present・PSD 用ラスター・エッジ拡張を、CPU の参照実装と突き合わせる |
| `SessionTests` | 作業セッション全体 | 開始でのプレビュー注入と終了での復元、保存時の一時復元、ジャーナルからの復旧、プロパティの判別と差し替え、選択レイヤー / 画像マスクレイヤーの合成、ぼかし、並べ替え・複製・削除・Undo、範囲選択の各モード、ベイク、PNG 出力とマテリアルへの適用、PSD の構造と読み直し、他ツールの PSD の拒否、メッシュが違う時の控えマスク、自動保存からの再開 |
| `UiTests` | ウィンドウとカプセル | UXML / USS が GUID で見つかる、Idle / 作業中 / 範囲選択中 / 再調整中の出し分け（日本語・英語）、入力欄からドキュメントへの書き込み、コードで使っている文言キーがすべて両言語にある |

- テストは一時的なオブジェクトと `Assets/DtxToolTestTemp` だけを使い、終わったら消す。実行すると進行中の作業セッションは終了する（内容は自動保存される）。
- 画面の見た目・シーン上のクリック操作・Unity 6 / URP / lilToon での表示は自動テストの対象外。

---

## 13. 仕様上の取り決めと、エディター上で確認が必要な点

| # | 項目 | 現在の実装 |
|---|------|------------|
| 1 | **BaseMap のアルファ** | 下地のアルファをそのまま出力し、レイヤーは RGB にだけ作用する（透過・カットアウト用アルファを壊さない） |
| 2 | PSD の **NormalMap フォルダは近似表示** | 外部ソフトでは置き換えの見え方になる。正確な結果は PNG 出力 |
| 3 | **外部ソフトで再保存した PSD** からは再開できない場合がある（§9.3） | DTXTool が出力した PSD を残す運用。必要になれば `_DTX_META` レイヤー方式を併用できる |
| 4 | **複数サブメッシュ** | 対象カードで選んだサブメッシュの三角形だけを、選択とエッジ拡張の対象にする |
| 5 | **Unity 6 / URP / lilToon** | コードは RP 非依存に書いてあるが、自動テストは 2022.3 × Built-in のみ。プレビューの見え方（特に lilToon のノーマル有効化）はエディター上で確認する |
| 6 | **Photoshop / CLIP STUDIO PAINT / GIMP / Krita での PSD** | 自前のリーダーで構造・画素・マスクの往復を、Pillow（別実装）でヘッダー・レイヤーの並び・統合画像の画素と向きを確認済み。各ソフトで実際に開く確認は未実施 |
