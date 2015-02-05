﻿//
// AbstractModel.cs
//
// Author:
//       Jonathan Lima <jonathan@pagar.me>
//
// Copyright (c) 2015 Pagar.me
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#if HAS_DYNAMIC
using System.Dynamic;
#endif

namespace PagarMe.Base
{
    public class AbstractModel
    #if HAS_DYNAMIC
        : DynamicObject
    #endif
    {
        private static Dictionary<string, Type> ModelMap = new Dictionary<string, Type>();

        static AbstractModel()
        {
            ModelMap.Add("object", typeof(AbstractModel));
            ModelMap.Add("transaction", typeof(Transaction));
            ModelMap.Add("card", typeof(Card));
            ModelMap.Add("customer", typeof(Customer));
            ModelMap.Add("plan", typeof(Plan));
            ModelMap.Add("subscription", typeof(Subscription));
        }

        private bool _loaded;
        private PagarMeService _service;
        private IDictionary<string, object> _keys;
        private IDictionary<string, object> _dirtyKeys;

        protected PagarMeService Service { get { return _service; } }

        public bool Loaded { get { return _loaded; } }

        public AbstractModel(PagarMeService service)
        {
            if (service == null)
                service = PagarMeService.GetDefaultService();

            _service = service;
            _keys = new Dictionary<string, object>();
            _dirtyKeys = new Dictionary<string, object>();
        }

        private object ConvertToken(JToken token)
        {
            object result = token;

            if (token is JArray)
            {
                result = ((JArray)token).Select(ConvertToken).ToArray();
            }
            else if (token is JObject)
            {
                var type = typeof(AbstractModel);
                var obj = (JObject)token;
                var typeNameProperty = obj.Property("object");

                if (typeNameProperty != null)
                if (!ModelMap.TryGetValue(typeNameProperty.Value.ToObject<string>(), out type))
                    type = typeof(AbstractModel);

                var model = (AbstractModel)Activator.CreateInstance(type, new object[] { _service });

                model.LoadFrom(obj);

                result = model;
            }
            else if (token is JValue)
            {
                result = ((JValue)token).Value;
            }

            return result;
        }

        private KeyValuePair<string, object> ConvertProperty(JProperty prop)
        {
            return new KeyValuePair<string, object>(prop.Name, ConvertToken(prop.Value));
        }

        internal void LoadFrom(string json)
        {
            LoadFrom(JObject.Parse(json));
        }

        internal void LoadFrom(AbstractModel model)
        {
            LoadFrom(model._keys);
        }

        internal void LoadFrom(JObject obj)
        {
            LoadFrom(obj.Properties().Select(ConvertProperty).ToDictionary((x) => x.Key, (x) => x.Value));
        }

        internal void LoadFrom(IDictionary<string, object> keys)
        {
            _keys = new Dictionary<string, object>(keys);

            CoerceTypes();
            ClearDirtyCache();

            _loaded = true;
        }

        protected virtual void CoerceTypes()
        {

        }

        protected virtual NestedModelSerializationRule SerializationRuleForField(string field, bool full)
        {
            return NestedModelSerializationRule.IdParameter;
        }

        protected void CoerceAttribute(string name, Type type)
        {
            object value;

            if (_keys.TryGetValue(name, out value))
                _keys[name] = CastAttribute(type, value);
        }

        internal KeyValuePair<string, object> ConvertKey(KeyValuePair<string, object> obj, bool full)
        {
            var key = obj.Key;
            var value = obj.Value;

            if (value is Array)
            {
                value = ((object[])value).Select((x) => ConvertKey(new KeyValuePair<string, object>("", x), full).Value);
            }
            else if (value is Model)
            {
                if (SerializationRuleForField(key, full) == NestedModelSerializationRule.IdParameter)
                {
                    key += "_id";
                    value = ((Model)value).Id;
                }
                else
                {
                    value = ((Model)value).GetKeys(full);
                }
            }
            else if (value is AbstractModel)
            {
                value = ((AbstractModel)value).GetKeys(full);
            }
            else if (value != null && value.GetType().GetTypeInfo().IsEnum)
            {
                value = EnumMagic.ConvertToString((Enum)value);
            }
            
            return new KeyValuePair<string, object>(key, value);
        }

        internal IDictionary<string, object> GetKeys(bool full)
        {
            IEnumerable<KeyValuePair<string, object>> keys;

            if (full)
                keys = _keys.Concat(_dirtyKeys);
            else
                keys = _dirtyKeys;

            keys = keys.Select((x) => ConvertKey(x, full));

            return keys.ToDictionary((x) => x.Key, (x) => x.Value);
        }

        internal string ToJson(bool full = false)
        {

            return JsonConvert.SerializeObject(GetKeys(full));
        }

        protected object CastAttribute(Type type, object obj)
        {
            var info = type.GetTypeInfo();

            if (obj != null && info.IsAssignableFrom(obj.GetType().GetTypeInfo()))
            {
                return obj;
            }
            else if (info.IsEnum)
            {
                return EnumMagic.ConvertFromString(type, obj.ToString());
            }
            else if (info.IsArray)
            {
                var elementType = type.GetElementType();
                var old = (Array)obj;
                var arr = Array.CreateInstance(elementType, old.Length);

                for (var i = 0; i < arr.Length; i++)
                    arr.SetValue(CastAttribute(elementType, old.GetValue(i)), i);

                return arr;
            }
            else if (info.IsPrimitive)
            {
                return Convert.ChangeType(obj, type);
            }
            else if (info.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                object[] args = obj == null ? null : new[] { CastAttribute(type.GetTypeInfo().GenericTypeParameters[0], obj) };
                return Activator.CreateInstance(type, args);
            }
            else if (obj != null && info.IsSubclassOf(typeof(AbstractModel)) && (obj.GetType() == typeof(AbstractModel) || obj.GetType().GetTypeInfo().IsSubclassOf(typeof(AbstractModel))))
            {
                var oldModel = (AbstractModel)obj;
                var model = (AbstractModel)Activator.CreateInstance
                    (type, new object[] { _service });

                model.LoadFrom(oldModel);

                return model;
            }

            return obj;
        }

        protected T CastAttribute<T>(object obj)
        {
            return (T)CastAttribute(typeof(T), obj);
        }

        protected T GetAttribute<T>(string name)
        {
            object result;

            if (!_dirtyKeys.TryGetValue(name, out result))
            if (!_keys.TryGetValue(name, out result))
                return default(T);

            return CastAttribute<T>(result);
        }

        protected void SetAttribute(string name, object value)
        {
            _dirtyKeys[name] = value;
        }

        protected void ClearDirtyCache()
        {
            _dirtyKeys.Clear();
        }

        public object this[string key]
        {
            get { return GetAttribute<object>(key); }
            set { SetAttribute(key, value); }
        }

        #if HAS_DYNAMIC
        private string ConvertKeyName(string input)
        {
            var result = "";

            for (var i = 0; i < input.Length; i++)
            {
                if (i > 0 && char.IsUpper(input[i]))
                    result += "_" + input[i];
                else
                    result += input[i];
            }

            return result.ToLowerInvariant();
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            var name = ConvertKeyName(binder.Name);

            if (_dirtyKeys.TryGetValue(name, out result))
                return true;

            if (_keys.TryGetValue(name, out result))
                return true;

            return false;
        }

        public override bool TrySetMember(SetMemberBinder binder, object value)
        {
            _dirtyKeys[ConvertKeyName(binder.Name)] = value;
            return true;
        }
        #endif
    }
}

