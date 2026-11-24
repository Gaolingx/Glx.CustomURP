using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class T8ShaderCheck
{
    const BindingFlags Methods=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    [Serializable] class MaterialResult {public string shader;public string[] keywordNames;public List<int> srpBatcherSubshaderCodes=new List<int>();public string[] messages;}
    [Serializable] class Result {public string api,unity;public List<MaterialResult> materials=new List<MaterialResult>();}
    public static void Run(){
        var batcher=typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode",Methods,null,new[]{typeof(Shader),typeof(int)},null);
        var r=new Result{api=SystemInfo.graphicsDeviceType.ToString(),unity=Application.unityVersion};
        foreach(var name in new[]{"Lit","SimpleLit","BakedLit","ComplexLit","Unlit"}){
            var path="Packages/com.unity.render-pipelines.universal/Shaders/"+name+".shader";
            var s=AssetDatabase.LoadAssetAtPath<Shader>(path);if(!s)throw new Exception(path);
            var mat=new Material(s); mat.SetPass(0);UnityEngine.Object.DestroyImmediate(mat);
            var row=new MaterialResult{shader=s.name,keywordNames=s.keywordSpace.keywordNames.OrderBy(k=>k).ToArray(),messages=ShaderUtil.GetShaderMessages(s).Select(m=>m.severity+": "+m.message).Distinct().ToArray()};
            for(int i=0;i<ShaderUtil.GetShaderData(s).SubshaderCount;i++)row.srpBatcherSubshaderCodes.Add((int)batcher.Invoke(null,new object[]{s,i}));
            r.materials.Add(row);
        }
        File.WriteAllText("ShaderReport.json",JsonUtility.ToJson(r,true));
    }
}
