// Copyright 2025 Savas Parastatidis
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Cvoya.Graph.Model.Serialization;

using System.Collections;

/// <summary>
/// Utility class for collection type analysis and manipulation.
/// </summary>
public static class CollectionTypeHelper
{
    public static Type GetElementTypeOrSelf(Type type)
    {
        if (type.IsArray) return type.GetElementType()!;
        if (type.IsGenericType && IsStandardCollectionInterface(type.GetGenericTypeDefinition()))
            return type.GetGenericArguments()[0];
        return type;
    }

    public static bool IsCollectionType(Type type)
    {
        if (type.IsArray) return true;
        if (type.IsGenericType) return IsStandardCollectionInterface(type.GetGenericTypeDefinition());
        return false;
    }

    public static Type GetElementType(Type collectionType)
    {
        if (collectionType.IsArray) return collectionType.GetElementType()!;
        if (collectionType.IsGenericType && IsStandardCollectionInterface(collectionType.GetGenericTypeDefinition()))
            return collectionType.GetGenericArguments()[0];
        throw new ArgumentException($"Type {collectionType.Name} is not a supported collection type", nameof(collectionType));
    }

    public static T ConvertToCollectionType<T>(List<object> items, Type elementType)
    {
        var targetType = typeof(T);
        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, items.Count);
            for (int i = 0; i < items.Count; i++) array.SetValue(items[i], i);
            return (T)(object)array;
        }

        if (targetType.IsGenericType && IsStandardCollectionInterface(targetType.GetGenericTypeDefinition()))
        {
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var item in items) list.Add(item);
            return (T)list;
        }

        throw new ArgumentException($"Unsupported collection type: {targetType.Name}");
    }

    private static bool IsStandardCollectionInterface(Type genericTypeDefinition)
    {
        return genericTypeDefinition == typeof(IEnumerable<>)
            || genericTypeDefinition == typeof(ICollection<>)
            || genericTypeDefinition == typeof(IList<>)
            || genericTypeDefinition == typeof(IReadOnlyCollection<>)
            || genericTypeDefinition == typeof(IReadOnlyList<>)
            || genericTypeDefinition == typeof(List<>)
            || genericTypeDefinition == typeof(IAsyncEnumerable<>);
    }
}
