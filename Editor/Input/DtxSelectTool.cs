using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// 作業用の EditorTool（Design state_and_lifecycle §5.5）。SceneView の入力の受け口。
    /// Unity の他のツールに切り替えると OnToolGUI が呼ばれなくなり、自動で一時停止扱いになる。
    /// </summary>
    [EditorTool("DTXTool")]
    internal sealed class DtxSelectTool : EditorTool
    {
        private GUIContent _icon;

        public override GUIContent toolbarIcon
            => _icon ??= new GUIContent(Icons.Get(Icons.Paint), "DTXTool");

        public override void OnActivated()
        {
            // 作業していない時に選ばれても何もできないので、元のツールに戻す
            if (!SessionController.IsWorking)
                EditorApplication.delayCall += () =>
                {
                    if (!SessionController.IsWorking && ToolManager.activeToolType == typeof(DtxSelectTool))
                        ToolManager.RestorePreviousPersistentTool();
                };
            else
                SettingsService.Set(SettingKeys.ScenePickPaused, false);
        }

        public override void OnToolGUI(EditorWindow window)
        {
            var s = SessionController.Current;
            if (s == null || !(window is SceneView view)) return;
            SceneInput.Handle(view, s);
        }
    }
}
