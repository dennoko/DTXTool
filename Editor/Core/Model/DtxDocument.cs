using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Dennoko.DTXTool.Core
{
    public enum BlendMode
    {
        Normal = 0,
        Multiply = 1,
        Additive = 2,
        Screen = 3,
        Overlay = 4,
    }

    public enum MaskSource
    {
        /// <summary>シーン / UV ビューで選んだ三角形（＋手塗り）。</summary>
        Selection = 0,
        /// <summary>外部のマスク画像。</summary>
        Image = 1,
        /// <summary>PSD に埋め込まれていたラスター（メッシュが変わって選択を復元できなかった時の代わり）。</summary>
        Embedded = 2,
    }

    public enum PlacementMode
    {
        Tiling = 0,
        Decal = 1,
    }

    public enum BackgroundBaseKind
    {
        Color = 0,
        Texture = 1,
    }

    public enum BackgroundNormalKind
    {
        Flat = 0,
        Texture = 1,
    }

    public enum EditMode
    {
        BaseAndNormal = 0,
        BaseOnly = 1,
    }

    /// <summary>アセットへの参照（GUID が第一、パスは GUID が変わった時の手がかり）。</summary>
    [Serializable]
    public sealed class TextureRef
    {
        public string guid = string.Empty;
        public string path = string.Empty;

        public bool IsEmpty => string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(path);

        public TextureRef Clone() => new TextureRef { guid = guid, path = path };
    }

    /// <summary>UV の変形。回転は度（反時計回り）。</summary>
    [Serializable]
    public sealed class UvTransform
    {
        public Vector2 tiling = Vector2.one;
        public Vector2 offset = Vector2.zero;
        public float rotation;

        public UvTransform Clone() => new UvTransform { tiling = tiling, offset = offset, rotation = rotation };
    }

    [Serializable]
    public sealed class MaskSpec
    {
        public MaskSource source = MaskSource.Selection;

        /// <summary>Selection: 三角形ビット列（TriangleBits 形式）。</summary>
        public int[] selectedBits = Array.Empty<int>();
        /// <summary>Selection: 手塗り（PaintRle 形式、4096²）。空なら手塗りなし。</summary>
        public byte[] paintRle = Array.Empty<byte>();

        /// <summary>Image: マスク画像。</summary>
        public TextureRef image = new TextureRef();

        /// <summary>
        /// 0 / 255 のマスクラスター（Deflate 圧縮、行 0 = v 0）。
        /// Embedded の時はこれがマスクそのもの。Selection の時は PSD へ書く時だけ入る控え（メッシュが変わった時の代わり）。
        /// </summary>
        public byte[] embedded = Array.Empty<byte>();
        public int embeddedSize;

        public bool invert;
        public float opacity = 1f;
        /// <summary>ぼかしの半径。テクスチャ幅に対する比率（解像度に依存しない）。</summary>
        public float feather;

        public bool HasEmbedded => embedded != null && embedded.Length > 0 && embeddedSize > 0;
    }

    [Serializable]
    public sealed class Layer
    {
        public string id = string.Empty;
        public string name = string.Empty;
        public bool visible = true;
        public float opacity = 1f;
        public BlendMode blendMode = BlendMode.Normal;

        public MaskSpec mask = new MaskSpec();

        // ベース: テクスチャ × 色（テクスチャが無ければ色だけ）
        public Color color = Color.white;
        public TextureRef baseTexture = new TextureRef();

        // ノーマル
        public TextureRef normalTexture = new TextureRef();
        public float normalScale = 1f;
        /// <summary>
        /// 下地の凹凸を消す。テクスチャがあれば下のノーマルを置き換え、無ければ平坦にする。
        /// false ならテクスチャを下のノーマルに RNM で重ねる。
        /// </summary>
        public bool replaceNormal;
        /// <summary>ノーマルだけに掛けるマスク画像（UV 全体に対応、輝度 × アルファ）。レイヤーのマスクに掛け合わせる。空なら掛けない。</summary>
        public TextureRef normalMask = new TextureRef();

        // 配置
        public PlacementMode placementMode = PlacementMode.Tiling;
        public UvTransform baseXform = new UvTransform();
        public bool syncNormal;
        public UvTransform normalXform = new UvTransform();
        public Vector2 decalCenter = new Vector2(0.5f, 0.5f);
        public Vector2 decalSize = new Vector2(0.25f, 0.25f);
        public float decalRotation;

        // 旧形式（ベースを「単色 / テクスチャ」、ノーマルを「なし / 平坦化 / マップ」で選んでいた頃）の値。
        // メモリ上のドキュメントや Undo の履歴に旧形式のまま残っていた時だけ 0 以上になり、
        // DocumentOps.MigrateLegacy が今の形へ直して -1 に戻す。
        [FormerlySerializedAs("fillKind")] public int legacyFillKind = -1;
        [FormerlySerializedAs("tint")] public Color legacyTint = Color.white;
        [FormerlySerializedAs("normalKind")] public int legacyNormalKind = -1;

        public bool HasBaseTexture => baseTexture != null && !baseTexture.IsEmpty;
        public bool HasNormalTexture => normalTexture != null && !normalTexture.IsEmpty;
        public bool HasNormalMask => normalMask != null && !normalMask.IsEmpty;
        /// <summary>ノーマルの合成に関わるか（テクスチャがある、または下地の凹凸を消す）。</summary>
        public bool AffectsNormal => HasNormalTexture || replaceNormal;

        /// <summary>ノーマルに使う変形（ベースのテクスチャがあり、同じ配置にしている時はベースと同じ）。</summary>
        public UvTransform EffectiveNormalXform => syncNormal && HasBaseTexture ? baseXform : normalXform;

        public static string NewId() => Guid.NewGuid().ToString("N");
    }

    [Serializable]
    public sealed class Background
    {
        public BackgroundBaseKind baseKind = BackgroundBaseKind.Color;
        public Color color = Color.white;
        public TextureRef baseTexture = new TextureRef();

        public BackgroundNormalKind normalKind = BackgroundNormalKind.Flat;
        public TextureRef normalTexture = new TextureRef();
    }

    /// <summary>
    /// 編集内容の真実。ピクセルは持たず、合成は毎回この内容から作り直す。
    /// Unity のシリアライズ（Undo）と DocumentCodec（PSD・自動保存）の両方で保存できる形にしてある。
    /// </summary>
    [Serializable]
    public sealed class DtxDocument
    {
        public const int CurrentFormatVersion = 1;
        public static readonly int[] Sizes = { 512, 1024, 2048, 4096 };

        public int formatVersion = CurrentFormatVersion;
        public EditMode editMode = EditMode.BaseAndNormal;
        public int width = 2048;
        public int height = 2048;
        public int uvChannel;

        // 対象メッシュ（選択三角形が有効かの判定に使う）
        public long meshHash;
        public int triangleCount;
        public int submeshIndex = -1;
        public string targetName = string.Empty;

        public Background background = new Background();
        /// <summary>添字 0 が最下層。</summary>
        public List<Layer> layers = new List<Layer>();

        public int IndexOf(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return -1;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].id == layerId) return i;
            return -1;
        }

        public Layer Find(string layerId)
        {
            int i = IndexOf(layerId);
            return i >= 0 ? layers[i] : null;
        }

        /// <summary>既にある名前と重ならない名前（「名前」「名前 2」…）。</summary>
        public string UniqueName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) baseName = "Layer";
            bool Exists(string n)
            {
                foreach (var l in layers) if (l.name == n) return true;
                return false;
            }
            if (!Exists(baseName)) return baseName;
            for (int i = 2; ; i++)
            {
                string n = baseName + " " + i;
                if (!Exists(n)) return n;
            }
        }
    }
}
