/*
 Created by Arseniy Kucherenko
 08.01.2016
 */


using System;
using System.Reflection;

namespace SignalingServer
{
	public class StringValue : System.Attribute
	{
		private string _value;

		public StringValue(string value)
		{
			_value = value;
		}

		public string Value
		{
			get { return _value; }
		}

		public static string GetStringValue(Enum value)
		{
			Type type = value.GetType();
			FieldInfo fi = type.GetField(value.ToString());
			StringValue[] attrs = fi.GetCustomAttributes(typeof(StringValue),false) as StringValue[];
			if (attrs.Length > 0)
				return attrs[0].Value;

			return null;
		}

	}
}

