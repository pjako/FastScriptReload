using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace FastScriptReload.Scripts.Runtime
{
    public static class TemporaryNewFieldValues
    {
        public delegate object GetNewFieldInitialValue(Type forNewlyGeneratedType);
        public delegate Type GetNewFieldType(Type forNewlyGeneratedType);

        private static readonly Dictionary<object, NewFieldValuesForType> _existingObjectToFiledNameValueMap = new Dictionary<object, NewFieldValuesForType>();
        private static readonly Dictionary<Type, Dictionary<string, GetNewFieldInitialValue>> _existingObjectTypeToFieldNameToCreateDetaultValueFn = new Dictionary<Type, Dictionary<string, GetNewFieldInitialValue>>();
        private static readonly Dictionary<Type, Dictionary<string, GetNewFieldType>> _existingObjectTypeToFieldNameToType = new Dictionary<Type, Dictionary<string, GetNewFieldType>>();
        
        //Unity by default will auto init some classes, like gradient, but those are not value types so need to be initialized manually
        private static Dictionary<Type, Func<object>> ReferenceTypeToCreateDefaultValueFn = new Dictionary<Type, Func<object>>()
        {
            [typeof(Gradient)] = () => new Gradient(),
            [typeof(AnimationCurve)] = () => new AnimationCurve(),
        };
        
        public static void RegisterNewFields(Type existingType, Dictionary<string, GetNewFieldInitialValue> fieldNameToGenerateDefaultValueFn, Dictionary<string, GetNewFieldType> fieldNameToGetTypeFn)
        {
            _existingObjectTypeToFieldNameToCreateDetaultValueFn[existingType] = fieldNameToGenerateDefaultValueFn;
            _existingObjectTypeToFieldNameToType[existingType] = fieldNameToGetTypeFn;
        }
        
        //Returns the added field's storage by ref, so the rewritten code can use it like a real field: read, assign, pass as ref / out
        public static ref TField ResolveField<TCreatedType, TField>(object original, string fieldName)
        {
            var fieldValues = ResolveFieldValues<TCreatedType>(original);
            if (!(fieldValues.TryGetValue(fieldName, out var box) && box is StrongBox<TField> typedBox))
            {
                //field not initialized yet, or its type changed since
                typedBox = new StrongBox<TField>();
                if (CreateInitialValue<TCreatedType>(original.GetType(), fieldName, typeof(TField)) is TField initialValue)
                {
                    typedBox.Value = initialValue;
                }
                fieldValues[fieldName] = typedBox;
            }

            return ref typedBox.Value;
        }
        
        public static bool TryGetDynamicallyAddedFieldValues(object forObject, out IDictionary<string, IStrongBox> addedFieldValues)
        {
            if (_existingObjectToFiledNameValueMap.TryGetValue(forObject, out var valuesForType))
            {
                addedFieldValues = valuesForType.Values;
                return true;
            }

            addedFieldValues = null;
            return false;
        }

        private static Dictionary<string, IStrongBox> ResolveFieldValues<TCreatedType>(object original)
        {
            if (!_existingObjectToFiledNameValueMap.TryGetValue(original, out var valuesForType))
            {
                valuesForType = new NewFieldValuesForType();
                _existingObjectToFiledNameValueMap[original] = valuesForType;
            }

            if (valuesForType.ForType != typeof(TCreatedType))
            {
                InitializeAdditionalFieldValues<TCreatedType>(original, valuesForType.Values);
                valuesForType.ForType = typeof(TCreatedType);
            }

            return valuesForType.Values;
        }

        private static void InitializeAdditionalFieldValues<TCreatedType>(object original, Dictionary<string, IStrongBox> fieldValues)
        {
            var originalType = original.GetType(); //TODO: PERF: resolve via TOriginal, not getType
            var fieldNameToType = _existingObjectTypeToFieldNameToType[originalType];

            //fields that were added in an earlier reload and removed since
            foreach (var removedFieldName in fieldValues.Keys.Where(n => !fieldNameToType.ContainsKey(n)).ToList())
            {
                fieldValues.Remove(removedFieldName);
            }
            
            foreach (var fieldNameToGetTypeFn in fieldNameToType)
            {
                var fieldType = fieldNameToGetTypeFn.Value(typeof(TCreatedType));
                var boxType = typeof(StrongBox<>).MakeGenericType(fieldType);
                if (fieldValues.TryGetValue(fieldNameToGetTypeFn.Key, out var existingBox) && existingBox.GetType() == boxType)
                {
                    continue; //keep value from previous reload
                }

                var box = (IStrongBox)Activator.CreateInstance(boxType);
                var initialValue = CreateInitialValue<TCreatedType>(originalType, fieldNameToGetTypeFn.Key, fieldType);
                if (fieldType.IsInstanceOfType(initialValue))
                {
                    box.Value = initialValue;
                }
                fieldValues[fieldNameToGetTypeFn.Key] = box;
            }
        }

        private static object CreateInitialValue<TCreatedType>(Type originalType, string fieldName, Type fieldType)
        {
            var initialValue = _existingObjectTypeToFieldNameToCreateDetaultValueFn[originalType][fieldName](typeof(TCreatedType));
            if (initialValue == null && ReferenceTypeToCreateDefaultValueFn.TryGetValue(fieldType, out var createValueFn))
            {
                initialValue = createValueFn();
            }

            return initialValue;
        }
    }

    public class NewFieldValuesForType {
        public Type ForType;
        public readonly Dictionary<string, IStrongBox> Values = new Dictionary<string, IStrongBox>();
    }
}