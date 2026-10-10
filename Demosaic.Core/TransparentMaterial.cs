using BepInEx.Logging;
using UnityEngine;

namespace DemosaicPlugin
{
    /// <summary>
    /// 透明材质工厂。
    /// 关键约束：材质全程唯一且永不主动销毁 —— 一旦销毁，已经替换到 Renderer 上的材质槽
    /// 就会指向已销毁对象（表现为紫块/黑块）。仅当运行时检测到被回收时才原地重建。
    /// </summary>
    internal sealed class TransparentMaterialProvider
    {
        private static readonly string[] ShaderCandidates =
        {
            "Universal Render Pipeline/Lit",
            "HDRP/Lit",
            "HD Render Pipeline/Lit",
            "Standard",
            "Standard (Specular setup)",
            "Unlit/Transparent",
            "Unlit/Color",
            "Sprites/Default"
        };

        private readonly ManualLogSource _log;
        private Shader _shader;
        private bool _shaderIsUrp;
        private Material _material;

        public TransparentMaterialProvider(ManualLogSource log)
        {
            _log = log;
        }

        public bool TryCreate()
        {
            if (_shader == null)
            {
                for (int i = 0; i < ShaderCandidates.Length; i++)
                {
                    _shader = Shader.Find(ShaderCandidates[i]);
                    if (_shader != null)
                    {
                        _shaderIsUrp = i == 0; // 只有第一个候选是 URP/Lit
                        break;
                    }
                }
            }

            if (_shader == null)
            {
                _log?.LogError("找不到任何可用着色器，Transparent / Smart 模式将降级为 Disable。");
                return false;
            }

            _material = Build(_shader, _shaderIsUrp);
            _log?.LogInfo($"透明材质创建成功，使用着色器: {_shader.name}");
            return true;
        }

        /// <summary>获取当前材质；若已被 Unity 回收则原地重建。</summary>
        public Material Get()
        {
            if (_material == null && _shader != null)
            {
                _material = Build(_shader, _shaderIsUrp);
                _log?.LogWarning("透明材质被回收，已自动重建（已替换的材质槽会重新指向新材质）。");
            }
            return _material;
        }

        private static Material Build(Shader shader, bool isUrp)
        {
            var mat = new Material(shader);
            // 防止 Unity GC 回收材质资源
            mat.hideFlags = HideFlags.HideAndDontSave;

            ApplyTransparentSettings(mat, shader, isUrp);
            return mat;
        }


        /// <summary>按 shader 分支写入透明设置（新建材质与就地改写共用同一套规则）。</summary>
        private static void ApplyTransparentSettings(Material mat, Shader shader, bool isUrp)
        {
            string shaderName = shader.name;

            if (isUrp)
            {
                // URP/Lit：切到透明 Surface 后**必须**把基础色 alpha 归零，
                // 否则得到的是一块不透明白板（旧版本遗漏了这一步）。
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetColor("_BaseColor", Color.clear);
            }
            else if (shaderName == "Sprites/Default")
            {
                // Sprites/Default 不支持 _Mode/_Surface，直接把颜色 alpha 设为 0
                mat.color = Color.clear;
            }
            else if (shaderName.Contains("Unlit"))
            {
                // Unlit 系列无光照属性，仅设置混合模式
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.color = Color.clear;
            }
            else
            {
                // Standard / Standard (Specular setup) / HDRP
                mat.SetFloat("_Mode", 3f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.color = Color.clear;
            }

            mat.renderQueue = 3000;
        }
    }
}
