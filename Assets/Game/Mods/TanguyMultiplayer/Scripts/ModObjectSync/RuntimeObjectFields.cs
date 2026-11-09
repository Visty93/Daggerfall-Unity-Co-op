// Bounded field-only discovery for existing objects/assets referenced by a mod.
// Never calls mod methods, property getters, custom enumerators or constructors.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DaggerfallCoop.ObjectSync
{
    public sealed class RuntimeObjectReference { public object Value; public string Path; public int Depth; }
    public static class RuntimeObjectFields
    {
        public static IEnumerable<RuntimeObjectReference> Read(object root,int maxDepth=5,int maxNodes=512)
        {
            if(root==null)yield break;
            var assembly=root.GetType().Assembly;var pending=new Stack<RuntimeObjectReference>();
            var seen=new HashSet<object>(ReferenceComparer.Instance);pending.Push(new RuntimeObjectReference{Value=root,Path=""});int visited=0;
            while(pending.Count>0&&visited++<maxNodes){
                var item=pending.Pop();if(item.Value==null||!seen.Add(item.Value))continue;
                yield return item;
                if(item.Depth>=maxDepth)continue;
                var type=item.Value.GetType();
                if(type.IsPrimitive||type.IsEnum||item.Value is string)continue;
                // A controller can hold another component from its own mod. Follow
                // its fields within the same bounds, but never traverse engine assets.
                if(item.Depth>0&&item.Value is UnityEngine.Object&&
                    (!(item.Value is Component)||type.Assembly!=assembly))continue;
                var array=item.Value as Array;
                if(array!=null){int i=0;foreach(var value in array){if(i>=128)break;pending.Push(new RuntimeObjectReference{Value=value,Path=item.Path+"["+(i++)+"]",Depth=item.Depth+1});}continue;}
                if(type.Assembly==typeof(List<>).Assembly){
                    var list=item.Value as IList;
                    if(list!=null){for(int i=Math.Min(list.Count,128)-1;i>=0;i--)pending.Push(new RuntimeObjectReference{Value=list[i],Path=item.Path+"["+i+"]",Depth=item.Depth+1});continue;}
                    var dictionary=item.Value as IDictionary;
                    if(dictionary!=null){int i=0;foreach(DictionaryEntry entry in dictionary){if(i++>=128)break;pending.Push(new RuntimeObjectReference{Value=entry.Value,Path=item.Path+"["+i+"]",Depth=item.Depth+1});}continue;}
                }
                if(type.Assembly!=assembly)continue;
                int fields=0;
                for(var declared=type;declared!=null&&declared!=typeof(object)&&declared!=typeof(MonoBehaviour)&&declared!=typeof(Component);declared=declared.BaseType)
                    foreach(var field in declared.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)){
                        if(fields++>=(item.Depth==0?256:64))break;
                        object value;try{value=field.GetValue(item.Value);}catch{continue;}
                        pending.Push(new RuntimeObjectReference{Value=value,Path=item.Path.Length==0?field.Name:item.Path+"."+field.Name,Depth=item.Depth+1});
                    }
            }
        }
        sealed class ReferenceComparer:IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance=new ReferenceComparer();
            public new bool Equals(object a,object b){return ReferenceEquals(a,b);}
            public int GetHashCode(object obj){return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);}
        }
        public static object Field(object value,string name)
        {
            if(value==null||string.IsNullOrEmpty(name))return null;
            for(var type=value.GetType();type!=null&&type!=typeof(MonoBehaviour);type=type.BaseType){
                var field=type.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
                if(field!=null)return field.GetValue(value);
            }
            return null;
        }
    }
}
