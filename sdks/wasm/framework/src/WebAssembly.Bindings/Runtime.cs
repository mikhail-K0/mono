﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WebAssembly {
	/// <summary>
	///   Provides access to the Mono/WebAssembly runtime to perform tasks like invoking JavaScript functions and retrieving global variables.
	/// </summary>
	public sealed class Runtime {
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		static extern string InvokeJS (string str, out int exceptional_result);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern object InvokeJSWithArgs (int js_obj_handle, string method, object [] _params, out int exceptional_result);
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern object GetObjectProperty (int js_obj_handle, string propertyName, out int exceptional_result);
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern object SetObjectProperty (int js_obj_handle, string propertyName, object value, bool createIfNotExists, bool hasOwnProperty, out int exceptional_result);
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern object GetGlobalObject (string globalName, out int exceptional_result);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern object ReleaseHandle (int js_obj_handle, out int exceptional_result);
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern object ReleaseObject (int js_obj_handle, out int exceptional_result);
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern object NewArrayJS (out int exceptional_result);
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern object NewObjectJS (int js_obj_handle, object [] _params, out int exceptional_result);

		/// <summary>
		/// Execute the provided string in the JavaScript context
		/// </summary>
		/// <returns>The js.</returns>
		/// <param name="str">String.</param>
		public static string InvokeJS (string str)
		{
			int exception;
			var res = InvokeJS (str, out exception);
			if (exception != 0)
				throw new JSException (res);
			return res;
		}

		static Dictionary<int, JSObject> bound_objects = new Dictionary<int, JSObject> ();
		static Dictionary<object, JSObject> raw_to_js = new Dictionary<object, JSObject> ();

		/// <summary>
		/// Creates a new JavaScript array object
		/// </summary>
		/// <returns>The JS Array.</returns>
		public static JSObject NewJSArray ()
		{
			int exception;
			var res = NewArrayJS (out exception);
			if (exception != 0)
				throw new JSException ((string)res);
			return res as JSObject;
		}

		/// <summary>
		/// Creates a new JavaScript object 
		/// </summary>
		/// <returns>The JSO bject.</returns>
		/// <param name="js_func_ptr">Js func ptr.</param>
		/// <param name="_params">Parameters.</param>
		public static JSObject NewJSObject (JSObject js_func_ptr = null, params object [] _params)
		{
			int exception;
			var res = NewObjectJS (js_func_ptr?.JSHandle ?? 0, _params, out exception);
			if (exception != 0)
				throw new JSException ((string)res);
			return res as JSObject;
		}

		static int BindJSObject (int js_id)
		{
			JSObject obj;
			if (bound_objects.ContainsKey (js_id))
				obj = bound_objects [js_id];
			else
				bound_objects [js_id] = obj = new JSObject (js_id);

			return (int)(IntPtr)obj.Handle;
		}

		static int UnBindJSObject (int js_id)
		{
			if (bound_objects.ContainsKey (js_id)) {
				var obj = bound_objects [js_id];
				bound_objects.Remove (js_id);
				return (int)(IntPtr)obj.Handle;
			}

			return 0;

		}

		static void UnBindJSObjectAndFree (int js_id)
		{
			if (bound_objects.TryGetValue (js_id, out var obj)) {
				bound_objects [js_id].RawObject = null;
				bound_objects.Remove (js_id);
				obj.JSHandle = -1;
				obj.IsDisposed = true;
				obj.RawObject = null;
				obj.Handle.Free ();

			}

		}


		static void UnBindRawJSObjectAndFree (int gcHandle)
		{

			GCHandle h = (GCHandle)(IntPtr)gcHandle;
			JSObject obj = (JSObject)h.Target;
			if (obj != null && obj.RawObject != null) {
				raw_to_js.Remove (obj.RawObject);

				int exception;
				ReleaseHandle (obj.JSHandle, out exception);
				if (exception != 0)
					throw new JSException ($"Error releasing handle on (js-obj js '{obj.JSHandle}' mono '{(IntPtr)obj.Handle} raw '{obj.RawObject != null})");

				// Calling Release Handle above only removes the reference from the JavaScript side but does not 
				// release the bridged JSObject associated with the raw object so we have to do that ourselves.
				obj.JSHandle = -1;
				obj.IsDisposed = true;
				obj.RawObject = null;

				obj.Handle.Free ();
			}

		}

		public static void FreeObject (object obj)
		{
			if (raw_to_js.TryGetValue (obj, out JSObject jsobj)) {
				raw_to_js [obj].RawObject = null;
				raw_to_js.Remove (obj);

				int exception;
				Runtime.ReleaseObject (jsobj.JSHandle, out exception);
				if (exception != 0)
					throw new JSException ($"Error releasing object on (raw-obj)");

				jsobj.JSHandle = -1;
				jsobj.RawObject = null;
				jsobj.IsDisposed = true;
				jsobj.Handle.Free ();

			} else {
				throw new JSException ($"Error releasing object on (obj)");
			}
		}

		static object CreateTaskSource (int js_id)
		{
			return new TaskCompletionSource<object> ();
		}

		static void SetTaskSourceResult (TaskCompletionSource<object> tcs, object result)
		{
			tcs.SetResult (result);
		}

		static void SetTaskSourceFailure (TaskCompletionSource<object> tcs, string reason)
		{
			tcs.SetException (new JSException (reason));
		}

		static int GetTaskAndBind (TaskCompletionSource<object> tcs, int js_id)
		{
			return BindExistingObject (tcs.Task, js_id);
		}

		static int BindExistingObject (object raw_obj, int js_id)
		{

			JSObject obj;
			if (raw_obj is JSObject)
				obj = (JSObject)raw_obj;
			else if (raw_to_js.ContainsKey (raw_obj))
				obj = raw_to_js [raw_obj];
			else
				raw_to_js [raw_obj] = obj = new JSObject (js_id, raw_obj);

			return (int)(IntPtr)obj.Handle;
		}

		static int GetJSObjectId (object raw_obj)
		{
			JSObject obj = null;
			if (raw_obj is JSObject)
				obj = (JSObject)raw_obj;
			else if (raw_to_js.ContainsKey (raw_obj))
				obj = raw_to_js [raw_obj];

			var js_handle = obj != null ? obj.JSHandle : -1;

			return js_handle;
		}

		static object GetMonoObject (int gc_handle)
		{
			GCHandle h = (GCHandle)(IntPtr)gc_handle;
			JSObject o = (JSObject)h.Target;
			if (o != null && o.RawObject != null)
				return o.RawObject;
			return o;
		}

		static object BoxInt (int i)
		{
			return i;
		}
		static object BoxDouble (double d)
		{
			return d;
		}

		static object BoxBool (int b)
		{
			return b == 0 ? false : true;
		}

		[StructLayout (LayoutKind.Explicit)]
		internal struct IntPtrAndHandle {
			[FieldOffset (0)]
			internal IntPtr ptr;

			[FieldOffset (0)]
			internal RuntimeMethodHandle handle;
		}

		//FIXME this probably won't handle generics
		static string GetCallSignature (IntPtr method_handle)
		{
			IntPtrAndHandle tmp = default (IntPtrAndHandle);
			tmp.ptr = method_handle;

			var mb = MethodBase.GetMethodFromHandle (tmp.handle);

			string res = "";
			foreach (var p in mb.GetParameters ()) {
				var t = p.ParameterType;

				switch (Type.GetTypeCode (t)) {
				case TypeCode.Byte:
				case TypeCode.SByte:
				case TypeCode.Int16:
				case TypeCode.UInt16:
				case TypeCode.Int32:
				case TypeCode.UInt32:
				case TypeCode.Boolean:
					// Enums types have the same code as their underlying numeric types
					if (t.IsEnum)
						res += "j";
					else
						res += "i";
					break;
				case TypeCode.Int64:
				case TypeCode.UInt64:
					// Enums types have the same code as their underlying numeric types
					if (t.IsEnum)
						res += "k";
					else
						res += "l";
					break;
				case TypeCode.Single:
					res += "f";
					break;
				case TypeCode.Double:
					res += "d";
					break;
				case TypeCode.String:
					res += "s";
					break;
				default:
					if (t.IsValueType)
						throw new Exception ("Can't handle VT arguments");
					res += "o";
					break;
				}
			}
			return res;
		}

		static object ObjectToEnum (IntPtr method_handle, int parm, object obj)
		{
			IntPtrAndHandle tmp = default (IntPtrAndHandle);
			tmp.ptr = method_handle;

			var mb = MethodBase.GetMethodFromHandle (tmp.handle);
			var parmType = mb.GetParameters () [parm].ParameterType;
			if (parmType.IsEnum)
				return Runtime.EnumFromExportContract (parmType, obj);
			else
				return null;

		}


		static MethodInfo gsjsc;
		static void GenericSetupJSContinuation<T> (Task<T> task, JSObject cont_obj)
		{
			task.GetAwaiter ().OnCompleted (() => {

				if (task.Exception != null)
					cont_obj.Invoke ("reject", task.Exception.ToString ());
				else {
					cont_obj.Invoke ("resolve", task.Result);
				}

				cont_obj.Dispose ();
				FreeObject (task);

			});
		}

		static void SetupJSContinuation (Task task, JSObject cont_obj)
		{
			if (task.GetType () == typeof (Task)) {
				task.GetAwaiter ().OnCompleted (() => {

					if (task.Exception != null)
						cont_obj.Invoke ("reject", task.Exception.ToString ());
					else
						cont_obj.Invoke ("resolve", null);

					cont_obj.Dispose ();
					FreeObject (task);
				});
			} else {
				//FIXME this is horrible codegen, we can do better with per-method glue
				if (gsjsc == null)
					gsjsc = typeof (Runtime).GetMethod ("GenericSetupJSContinuation", BindingFlags.NonPublic | BindingFlags.Static);
				gsjsc.MakeGenericMethod (task.GetType ().GetGenericArguments ()).Invoke (null, new object [] { task, cont_obj });
			}
		}


		/// <summary>
		///   Fetches a global object from the Javascript world, either from the current brower window or from the node.js global context.
		/// </summary>
		/// <remarks>
		///   This method returns the value of a global object marshalled for consumption in C#.
		/// </remarks>
		/// <returns>
		///   <para>
		///     The return value can either be a primitive (string, int, double), a 
		///     <see cref="T:WebAssembly.JSObject"/> for JavaScript objects, a 
		///     <see cref="T:System.Threading.Tasks.Task"/>(object) for JavaScript promises, an array of
		///     a byte, int or double (for Javascript objects typed as ArrayBuffer) or a 
		///     <see cref="T:System.Func"/> to represent JavaScript functions.  The specific version of
		///     the Func that will be returned depends on the parameters of the Javascript function
		///     and return value.
		///   </para>
		///   <para>
		///     The value of a returned promise (The Task(object) return) can in turn be any of the above
		///     valuews.
		///   </para>
		/// </returns>
		/// <param name="str">The name of the global object, or null if you want to retrieve the 'global' object itself.
		/// On a browser, this is the 'window' object, on node.js it is the 'global' object.
		/// </param>
		public static object GetGlobalObject (string str = null)
		{
			int exception;
			var globalHandle = Runtime.GetGlobalObject (str, out exception);

			if (exception != 0)
				throw new JSException ($"Error obtaining a handle to global {str}");

			return globalHandle;
		}

		static string ObjectToString (object o)
		{

			if (o is Enum)
				return EnumToExportContract ((Enum)o).ToString ();

			return o.ToString ();
		}

		// This is simple right now and will include FlagsAttribute later.
		public static Enum EnumFromExportContract (Type enumType, object value)
		{

			if (!enumType.IsEnum) {
				throw new ArgumentException ("Type provided must be an Enum.", nameof (enumType));
			}

			if (value is string) {

				var fields = enumType.GetFields ();
				foreach (var fi in fields) {
					// Do not process special names
					if (fi.IsSpecialName)
						continue;

					ExportAttribute [] attributes =
					    (ExportAttribute [])fi.GetCustomAttributes (typeof (ExportAttribute), false);

					var enumConversionType = ConvertEnum.Default;

					object contractName = null;

					if (attributes != null && attributes.Length > 0) {
						enumConversionType = attributes [0].EnumValue;
						if (enumConversionType != ConvertEnum.Numeric)
							contractName = attributes [0].ContractName;

					}

					if (contractName == null)
						contractName = fi.Name;

					switch (enumConversionType) {
					case ConvertEnum.ToLower:
						contractName = contractName.ToString ().ToLower ();
						break;
					case ConvertEnum.ToUpper:
						contractName = contractName.ToString ().ToUpper ();
						break;
					case ConvertEnum.Numeric:
						contractName = (int)Enum.Parse (value.GetType (), contractName.ToString ());
						break;
					default:
						contractName = contractName.ToString ();
						break;
					}

					if (contractName.ToString () == value.ToString ()) {
						return (Enum)Enum.Parse (enumType, fi.Name);
					}

				}

				throw new ArgumentException ($"Value is a name, but not one of the named constants defined for the enum of type: {enumType}.", nameof (value));
			} else {
				return (Enum)Enum.ToObject (enumType, value);
			}

		}

		// This is simple right now and will include FlagsAttribute later.
		public static object EnumToExportContract (Enum value)
		{

			FieldInfo fi = value.GetType ().GetField (value.ToString ());

			ExportAttribute [] attributes =
			    (ExportAttribute [])fi.GetCustomAttributes (typeof (ExportAttribute), false);

			var enumConversionType = ConvertEnum.Default;

			object contractName = null;

			if (attributes != null && attributes.Length > 0) {
				enumConversionType = attributes [0].EnumValue;
				if (enumConversionType != ConvertEnum.Numeric)
					contractName = attributes [0].ContractName;

			}

			if (contractName == null)
				contractName = value;

			switch (enumConversionType) {
			case ConvertEnum.ToLower:
				contractName = contractName.ToString ().ToLower ();
				break;
			case ConvertEnum.ToUpper:
				contractName = contractName.ToString ().ToUpper ();
				break;
			case ConvertEnum.Numeric:
				contractName = (int)Enum.Parse (value.GetType (), contractName.ToString ());
				break;
			default:
				contractName = contractName.ToString ();
				break;
			}

			return contractName;
		}

	}
}
