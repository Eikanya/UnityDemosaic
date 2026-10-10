using UnityEngine;
#if DEMOSAIC_IL2CPP
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif

namespace DemosaicPlugin
{
    /// <summary>
    /// 隔离 Mono / IL2CPP 的类型差异，使共享代码只面对托管数组与托管类型。
    /// </summary>
    internal static class RuntimeInterop
    {
        /// <summary>读取 Renderer 的共享材质，统一为托管数组（IL2CPP 会做一次拷贝）。</summary>
        public static Material[] GetSharedMaterials(Renderer renderer)
        {
            if (renderer == null) return null;

#if DEMOSAIC_IL2CPP
            var source = renderer.sharedMaterials;
            if (source == null) return null;
            var result = new Material[source.Length];
            for (int i = 0; i < source.Length; i++)
                result[i] = source[i];
            return result;
#else
            return renderer.sharedMaterials;
#endif
        }

        /// <summary>写回 Renderer 的共享材质。</summary>
        public static void SetSharedMaterials(Renderer renderer, Material[] materials)
        {
            if (renderer == null || materials == null) return;

#if DEMOSAIC_IL2CPP
            var target = new Il2CppReferenceArray<Material>(materials.Length);
            for (int i = 0; i < materials.Length; i++)
                target[i] = materials[i];
            renderer.sharedMaterials = target;
#else
            renderer.sharedMaterials = materials;
#endif
        }

        public static GameObject AsGameObject(UnityEngine.Object obj)
        {
#if DEMOSAIC_IL2CPP
            return obj != null ? obj.TryCast<GameObject>() : null;
#else
            return obj as GameObject;
#endif
        }

        public static Component AsComponent(UnityEngine.Object obj)
        {
#if DEMOSAIC_IL2CPP
            return obj != null ? obj.TryCast<Component>() : null;
#else
            return obj as Component;
#endif
        }

        /// <summary>反射取回的引擎对象转 MonoBehaviour（IL2CPP 下托管 <c>as</c> 不生效）。</summary>
        public static MonoBehaviour AsMonoBehaviour(object obj)
        {
#if DEMOSAIC_IL2CPP
            return obj is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase baseObj ? baseObj.TryCast<MonoBehaviour>() : null;
#else
            return obj as MonoBehaviour;
#endif
        }

        /// <summary>任意对象转 Mesh（类型不符返回 null，不做硬转）。</summary>
 
        /// <summary>反射取回的对象转 Material。</summary>
        public static Material AsMaterial(object obj)
        {
#if DEMOSAIC_IL2CPP
            return obj is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase baseObj ? baseObj.TryCast<Material>() : null;
#else
            return obj as Material;
#endif
        }

        public static SkinnedMeshRenderer AsSkinnedMeshRenderer(Renderer renderer)
        {
#if DEMOSAIC_IL2CPP
            return renderer != null ? renderer.TryCast<SkinnedMeshRenderer>() : null;
#else
            return renderer as SkinnedMeshRenderer;
#endif
        }

        /// <summary>组件类型名：IL2CPP 必须走 Il2Cpp 类型，托管 GetType() 只会得到代理类型。</summary>
        public static string GetComponentTypeName(Component component)
        {
            if (component == null) return null;
#if DEMOSAIC_IL2CPP
            return component.GetIl2CppType().Name;
#else
            return component.GetType().Name;
#endif
        }

        /// <summary>取 Renderer 使用的网格名（SkinnedMeshRenderer 优先，其次 MeshFilter）。</summary>
        public static string GetMeshName(Renderer renderer)
        {
            if (renderer == null) return null;

            var skinned = AsSkinnedMeshRenderer(renderer);
            if (skinned != null && skinned.sharedMesh != null)
                return skinned.sharedMesh.name;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : null;
        }

        /// <summary>取 GameObject 上挂载的网格名。</summary>
        public static string GetMeshName(GameObject go)
        {
            if (go == null) return null;

            var skinned = go.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null && skinned.sharedMesh != null)
                return skinned.sharedMesh.name;

            var filter = go.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : null;
        }
    }
}
