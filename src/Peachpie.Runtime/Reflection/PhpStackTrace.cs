﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Pchp.Core.Reflection
{
    /// <summary>
    /// Represents a stack trace in context of PHP application.
    /// </summary>
    /// <remarks>PHP stack trace differs from CLR stack trace in several ways:
    /// - global code shown as <c>{main}</c>
    /// - there are CLR frames that we don't want to expose in PHP (mostly annotated with [DebuggerNonUserCode])
    /// - frame location specifies the called function location</remarks>
    [DebuggerNonUserCode]
    public sealed class PhpStackTrace
    {
        sealed class FrameLine
        {
            /// <summary>
            /// The frame index.
            /// </summary>
            readonly int _order;

            /// <summary>
            /// the frame with location.
            /// </summary>
            readonly PhpStackFrame _locationFrame;

            /// <summary>
            /// The frame with called routine.
            /// </summary>
            readonly PhpStackFrame _calledFrame;

            public bool HasLocation => _locationFrame != null && _locationFrame.HasLocation;

            public FrameLine(int order, PhpStackFrame locationFrame, PhpStackFrame calledFrame)
            {
                _order = order;
                _locationFrame = locationFrame;
                _calledFrame = calledFrame;
            }

            public PhpArray ToUserFrame()
            {
                var item = new PhpArray();

                if (_locationFrame != null && _locationFrame.HasLocation)
                {
                    //file    string The current file name.See also __FILE__.
                    item.Add("file", _locationFrame.FileName);

                    //line integer The current line number. See also __LINE__.
                    item.Add("line", _locationFrame.Line);
                    item.Add("column", _locationFrame.Column);
                }

                if (_calledFrame != null)
                {
                    //function    string The current function name.See also __FUNCTION__.
                    item.Add("function", _calledFrame.RoutineName);

                    var tname = _calledFrame.TypeName;
                    if (tname != null)
                    {
                        //class   string The current class name. See also __CLASS__
                        item.Add("class", tname);
                        //object object The current object.
                        //type string The current call type.If a method call, "->" is returned.If a static method call, "::" is returned.If a function call, nothing is returned.
                        item.Add("type", _calledFrame.MethodOperator);
                    }
                }

                //args array   If inside a function, this lists the functions arguments.If inside an included file, this lists the included file name(s).

                return item;
            }

            public string ToStackTraceLine()
            {
                var result = new StringBuilder();

                if (_order >= 0)
                {
                    result.Append('#');
                    result.Append(_order);
                    result.Append(' ');
                }

                if (HasLocation)
                {
                    result.AppendFormat("{0}({1},{2})", _locationFrame.FileName, _locationFrame.Line, _locationFrame.Column);
                }

                if (_calledFrame != null)
                {
                    result.Append(": ");
                    result.Append(_calledFrame.RoutineFullName);
                    result.Append(_calledFrame.RoutineParameters);
                }

                return result.ToString();
            }

            public override string ToString() => ToStackTraceLine();
        }

        PhpStackFrame[] _frames;

        [DebuggerNonUserCode]
        public PhpStackTrace()
        {
            // collect stack trace if possible:
#if NET46
            InitPhpStackFrames(new StackTrace(true));
#else
            // as of now, StackTrace can only be constructed from thrown exception:
            try
            {
                throw new Exception();
            }
            catch (Exception ex)
            {
                InitPhpStackFrames(new StackTrace(ex, true));
            }
#endif
            Debug.Assert(_frames != null);
        }

        internal PhpStackTrace(StackTrace clrtrace)
        {
            InitPhpStackFrames(clrtrace);
        }

        void InitPhpStackFrames(StackTrace clrtrace)
        {
            _frames = clrtrace.GetFrames()
                .Where(IsPhpStackFrame)
                .Select(clrframe => new PhpStackFrame(clrframe))
                .ToArray();
        }

        static bool IsPhpStackFrame(StackFrame frame)
        {
            var method = frame.GetMethod();
            if (method == null)
            {
                return false;
            }

            // weird method names:
            if (!ReflectionUtils.IsAllowedPhpName(method.Name) && method.Name != ReflectionUtils.GlobalCodeMethodName)
            {
                return false;
            }

            // <Script> type
            var tinfo = method.DeclaringType.GetTypeInfo();
            if (tinfo.Name == "<Script>")
            {
                return false;
            }

            // in Peachpie assemblies (runtime, libraries) // implicitly NonUserCode
            var ass = tinfo.Assembly;
            var token = ass.GetName().GetPublicKeyToken();
            if (token != null && Utilities.StringUtils.BinToHex(token) == ReflectionUtils.PeachpieAssemblyTokenKey)
            {
                // but allow library functions
                if (method.IsPublic && method.IsStatic && tinfo.IsPublic && tinfo.IsAbstract) // public static class + public static method
                {
                    // ok
                }
                else
                {
                    return false;
                }
            }

            // [DebuggerNonUserCodeAttribute] or [DebuggerHiddenAttribute]
            if (method.GetCustomAttribute<DebuggerNonUserCodeAttribute>() != null || method.GetCustomAttribute<DebuggerHiddenAttribute>() != null ||
                tinfo.GetCustomAttribute<DebuggerNonUserCodeAttribute>() != null)
            {
                return false;
            }

            //
            return true;
        }

        public string GetFilename() => (_frames.Length != 0 && _frames[0].HasLocation) ? _frames[0].FileName : string.Empty;

        public int GetLine() => (_frames.Length != 0 && _frames[0].HasLocation) ? _frames[0].Line : 0;

        FrameLine[] GetLines()
        {
            var frames = _frames;
            var lines = new FrameLine[frames.Length + 1];
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = new FrameLine(i - 1,
                    locationFrame: (i < frames.Length) ? frames[i] : null,
                    calledFrame: (i > 0) ? frames[i - 1] : null);
            }

            return lines;
        }

        /// <summary>
        /// Gets stack trace as string.
        /// </summary>
        public string GetStackTraceString()
        {
            var lines = GetLines();
            var result = new StringBuilder();

            for (int i = 1; i < lines.Length; i++)
            {
                result.AppendLine(lines[i].ToStackTraceLine());
            }

            return result.ToString();
        }

        /// <summary>
        /// Gets PHP `backtrace`. See <c>debug_backtrace()</c>.
        /// </summary>
        public PhpArray GetBacktrace(int skip = 0, int limit = int.MaxValue)
        {
            if (skip < 0) throw new ArgumentOutOfRangeException();

            var lines = GetLines();
            var arr = new PhpArray();
            for (int i = 1 + skip; i < lines.Length - 1 && arr.Count < limit; i++)
            {
                arr.Add((PhpValue)lines[i].ToUserFrame());
            }

            return arr;
        }

        /// <summary>
        /// Gets exception string.
        /// </summary>
        public string FormatExceptionString(string exceptionname, string message)
        {
            var result = new StringBuilder();

            // TODO: to resources

            // {exceptionname} in {location}
            // Stack trace:
            // #0 ...
            var lines = GetLines();

            result.Append(exceptionname);

            if (!string.IsNullOrEmpty(message))
            {
                result.Append(": ");
                result.Append(message);
            }

            if (lines.Length != 0)
            {
                if (lines[0].HasLocation)
                {
                    result.Append(" in ");
                    result.Append(lines[0].ToStackTraceLine());
                }

                if (lines.Length > 1)
                {
                    result.AppendLine();
                    result.AppendLine("Stack trace:");
                    for (int i = 1; i < lines.Length; i++)
                    {
                        result.AppendLine(lines[i].ToStackTraceLine());
                    }
                }
            }

            return result.ToString();
        }
    }

    internal sealed class PhpStackFrame
    {
        const string GlobalCodeName = "{main}";
        const string UnknownFile = "<unknown>";

        readonly StackFrame _clrframe;

        /// <summary>
        /// Gets file path where the frame points to.
        /// Can be <c>null</c>.
        /// </summary>
        public string FileName => _clrframe.GetFileName();

        public bool HasLocation => FileName != null;

        public int Line => _clrframe.GetFileLineNumber() - 1;

        public int Column => _clrframe.GetFileColumnNumber() - 1;

        public string RoutineName
        {
            get
            {
                var method = _clrframe.GetMethod();
                if (method.IsStatic)
                {
                    if (method.Name == ReflectionUtils.GlobalCodeMethodName)
                    {
                        // global code
                        return GlobalCodeName;
                    }
                }

                return method.Name;
            }
        }

        public string TypeName
        {
            get
            {
                var method = _clrframe.GetMethod();
                var tinfo = method.DeclaringType.GetTypeInfo();

                if (method.IsStatic)
                {
                    if (method.Name == ReflectionUtils.GlobalCodeMethodName)
                    {
                        // global code
                        return null;
                    }

                    if (tinfo.GetCustomAttribute<ScriptAttribute>() != null)
                    {
                        // global function
                        return null;
                    }

                    if (tinfo.IsPublic && tinfo.IsAbstract) // => public static
                    {
                        if (tinfo.Assembly.GetCustomAttribute<PhpExtensionAttribute>() != null)
                        {
                            // library function
                            return null;
                        }
                    }
                }

                return tinfo.Name;
            }
        }

        public string MethodOperator => _clrframe.GetMethod().IsStatic ? "::" : "->";

        public string RoutineFullName
        {
            get
            {
                var method = _clrframe.GetMethod();
                var typename = TypeName;
                if (typename != null)
                {
                    return string.Concat(typename, MethodOperator, method.Name);
                }
                else
                {
                    return RoutineName;
                }
            }
        }

        public string RoutineParameters
        {
            get
            {
                if (_clrframe.GetMethod().Name == ReflectionUtils.GlobalCodeMethodName)
                {
                    return string.Empty;
                }

                // note: parameter values not supported, user should use debugger
                var str = new StringBuilder();

                str.Append('(');

                bool first = true;

                foreach (var p in _clrframe.GetMethod().GetParameters())
                {
                    if (first && ReflectionUtils.IsImplicitParameter(p))
                    {
                        continue;
                    }

                    if (!first)
                    {
                        str.Append(", ");
                    }
                    else
                    {
                        first = false;
                    }

                    str.Append('$');
                    str.Append(p.Name);
                }

                str.Append(')');

                return str.ToString();
            }
        }

        public PhpStackFrame(StackFrame clrframe)
        {
            _clrframe = clrframe;
        }

        public override string ToString()
        {
            return string.Format("{0}:({1},{2}): {3}{4}",
                FileName ?? "<unknown>",
                Line, Column,
                RoutineName,
                RoutineParameters);
        }
    }
}