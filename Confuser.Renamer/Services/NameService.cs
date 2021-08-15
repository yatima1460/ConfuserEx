using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Confuser.Core;
using Confuser.Core.Services;
using Confuser.Renamer.Analyzers;
using Confuser.Renamer.Properties;
using dnlib.DotNet;
using Microsoft.Extensions.DependencyInjection;

namespace Confuser.Renamer.Services {
	internal class NameService : INameService {
		static readonly object CanRenameKey = new object();
		static readonly object RenameModeKey = new object();
		static readonly object ReferencesKey = new object();
		static readonly object OriginalFullNameKey = new object();
		static readonly object IsRenamedKey = new object();

		private readonly ReadOnlyMemory<byte> nameSeed;
		readonly IRandomGenerator random;
		readonly VTableStorage storage;
		private readonly NameProtection _parent;

		readonly HashSet<string> identifiers = new HashSet<string>();

		readonly byte[] nameId = new byte[8];
		readonly Dictionary<string, string> _originalToObfuscatedNameMap = new Dictionary<string, string>();
		readonly Dictionary<string, string> _obfuscatedToOriginalNameMap = new Dictionary<string, string>();
		internal ReversibleRenamer reversibleRenamer;

		internal NameService(IServiceProvider provider, NameProtection parent) {
			_parent = parent ?? throw new ArgumentNullException(nameof(parent));
			storage = new VTableStorage(provider);
			random = provider.GetRequiredService<IRandomService>().GetRandomGenerator(NameProtection._FullId);
			nameSeed = random.NextBytes(20);

			Renamers = ImmutableArray.Create<IRenamer>(
				new InterReferenceAnalyzer(),
				new VTableAnalyzer(),
				new TypeBlobAnalyzer(),
				new ResourceAnalyzer(),
				new LdtokenEnumAnalyzer(),
				new ManifestResourceAnalyzer(),
				new ReflectionAnalyzer(),
				new CallSiteAnalyzer()
			);
		}

		public IImmutableList<IRenamer> Renamers { get; private set; }

		public VTableStorage GetVTables() {
			return storage;
		}

		public bool CanRename(IConfuserContext context, IDnlibDef def) {
			if (context == null) throw new ArgumentNullException(nameof(context));

			if (def == null || !context.GetParameters(def).HasParameters(_parent))
				return false;

			return context.Annotations.Get(def, CanRenameKey, true);
		}

		public void SetCanRename(IConfuserContext context, IDnlibDef def, bool val) {
			if (context == null) throw new ArgumentNullException(nameof(context));
			if (def == null) throw new ArgumentNullException(nameof(def));

			context.Annotations.Set(def, CanRenameKey, val);
		}

		public void SetParam<T>(IConfuserContext context, IDnlibDef def, IProtectionParameter<T> protectionParameter, T value) {
			string serializedValue = protectionParameter.Serialize(value);
			context.GetParameters(def).SetParameter(_parent, protectionParameter.Name, serializedValue);
		}

		public T GetParam<T>(IConfuserContext context, IDnlibDef def, IProtectionParameter<T> protectionParameter) {
			var parameters = context.GetParameters(def);
			if (!parameters.HasParameter(_parent, protectionParameter.Name)) return protectionParameter.DefaultValue;
			
			string value = context.GetParameters(def).GetParameter(_parent, protectionParameter.Name);
			return protectionParameter.Deserialize(value);
		}

		public RenameMode GetRenameMode(IConfuserContext context, object obj) {
			return context.Annotations.Get(obj, RenameModeKey, RenameMode.Unicode);
		}

		public void SetRenameMode(IConfuserContext context, object obj, RenameMode val) {
			context.Annotations.Set(obj, RenameModeKey, val);
		}

		public void ReduceRenameMode(IConfuserContext context, object obj, RenameMode val) {
			RenameMode original = GetRenameMode(context, obj);
			if (original < val)
				context.Annotations.Set(obj, RenameModeKey, val);
			if (val <= RenameMode.Reflection && obj is IDnlibDef dnlibDef) {
				string nameWithoutParams = GetSimplifiedFullName(context, dnlibDef, true);
				SetOriginalName(context, dnlibDef, nameWithoutParams);
			}
		}

		public void AddReference<T>(IConfuserContext context, T obj, INameReference<T> reference) {
			context.Annotations.GetOrCreate(obj, ReferencesKey, key => new List<INameReference>()).Add(reference);
		}

		public void Analyze(IConfuserContext context, IDnlibDef def) {
			var analyze = context.Pipeline.FindPhase<AnalyzePhase>();

			SetOriginalName(context, def, def.Name);
			if (def is TypeDef typeDef) {
				GetVTables().GetVTable(typeDef);
			}

			analyze.Analyze(this, context, EmptyProtectionParameters.Instance, def, true);
		}
		
		public void SetNameId(uint id) {
			for (int i = nameId.Length - 1; i >= 0; i--) {
				nameId[i] = (byte)(id & 0xff);
				id >>= 8;
			}
		}

		public void IncrementNameId() {
			for (int i = nameId.Length - 1; i >= 0; i--) {
				nameId[i]++;
				if (nameId[i] != 0)
					break;
			}
		}

		string ObfuscateNameInternal(ReadOnlySpan<byte> hash, RenameMode mode) {
			switch (mode) {
				case RenameMode.Empty:
					return "";
				case RenameMode.Unicode:
					return Utils.EncodeString(hash, unicodeCharset) + "\u202e";
				case RenameMode.Letters:
					return Utils.EncodeString(hash, letterCharset);
				case RenameMode.ASCII:
					return Utils.EncodeString(hash, asciiCharset);
				case RenameMode.Reflection:
					return Utils.EncodeString(hash, reflectionCharset);
				case RenameMode.Decodable:
					IncrementNameId();
					return "_" + Utils.EncodeString(hash, alphaNumCharset);
				case RenameMode.Sequential:
					IncrementNameId();
					return "_" + Utils.EncodeString(nameId, alphaNumCharset);
				default:
					throw new NotSupportedException("Rename mode '" + mode + "' is not supported.");
			}
		}

		string ParseGenericName(string name, out int count) {
			int graveIndex = name.LastIndexOf('`');
			if (graveIndex != -1) {
				if (int.TryParse(name.Substring(graveIndex + 1), out int c)) {
					count = c;
					return name.Substring(0, graveIndex);
				}
			}

			count = 0;
			return name;
		}

		string MakeGenericName(string name, int count) => count == 0 ? name : $"{name}`{count}";

		public string ObfuscateName(string name, RenameMode mode) => ObfuscateName(null, name, mode, false);

		public string ObfuscateName(IConfuserContext context, IDnlibDef dnlibDef, RenameMode mode) {
			var originalFullName = GetOriginalFullName(context, dnlibDef);
			bool preserveGenericParams = GetParam(context, dnlibDef, _parent.Parameters.PreserveGenericParams);
			return ObfuscateName(null, originalFullName, mode, preserveGenericParams);
		}

		public string ObfuscateName(string format, string name, RenameMode mode, bool preserveGenericParams = false) {
			int genericParamsCount = 0;
			if (preserveGenericParams) {
				name = ParseGenericName(name, out genericParamsCount);
			}

			string newName;

			if (string.IsNullOrEmpty(name) || mode == RenameMode.Empty)
				return string.Empty;

			if (mode == RenameMode.Debug || mode == RenameMode.Retain)
			{
				// When flattening there are issues, in case there is a . in the name of the assembly.
				newName = name.Replace('.', '_');
				newName = mode == RenameMode.Debug ? "_" + newName : newName;
			}
			else if (mode == RenameMode.Reversible)
			{
				if (reversibleRenamer == null)
					throw new ArgumentException("Password not provided for reversible renaming.");
				newName = reversibleRenamer.Encrypt(name);
			}
			else if (!_originalToObfuscatedNameMap.TryGetValue(name, out newName))
			{
				byte[] hash = Utils.Xor(Utils.SHA1(Encoding.UTF8.GetBytes(name)), nameSeed.Span);
				while (true) {
					newName = ObfuscateNameInternal(hash, mode);

					try {
						if (!(format is null))
							newName = string.Format(CultureInfo.InvariantCulture, format, newName);
					}
					catch (FormatException ex) {
						throw new ArgumentException(
							string.Format(CultureInfo.InvariantCulture,
								Resources.NameService_ObfuscateName_InvalidFormat, format),
							nameof(format), ex);
					}

					if (!identifiers.Contains(MakeGenericName(newName, genericParamsCount))
					    && !_obfuscatedToOriginalNameMap.ContainsKey(newName))
						break;
					hash = Utils.SHA1(hash);
				}

				if (mode == RenameMode.Decodable || mode == RenameMode.Sequential) {
					_obfuscatedToOriginalNameMap.Add(newName, name);
					_originalToObfuscatedNameMap.Add(name, newName);
				}
			}

			return MakeGenericName(newName, genericParamsCount);
		}

		public string RandomName() {
			return RandomName(RenameMode.Unicode);
		}

		public string RandomName(RenameMode mode) {
			Span<byte> buf = stackalloc byte[16];
			random.NextBytes(buf);
			return ObfuscateName(Utils.ToHexString(buf), mode);
		}

		public void SetOriginalName(IConfuserContext context, IDnlibDef dnlibDef, string newFullName = null) {
			AddReservedIdentifier(dnlibDef.Name);
			if (dnlibDef is TypeDef typeDef) {
				AddReservedIdentifier(typeDef.Namespace);
			}
			string fullName = newFullName ?? GetSimplifiedFullName(context, dnlibDef);
			context.Annotations.Set(dnlibDef, OriginalFullNameKey, fullName);
		}

		public void AddReservedIdentifier(string id) => identifiers.Add(id);

		public void RegisterRenamer(IRenamer renamer) {
			Renamers = Renamers.Add(renamer);
		}

		public T FindRenamer<T>() {
			return Renamers.OfType<T>().Single();
		}

		public void MarkHelper(IConfuserContext context, IDnlibDef def, IMarkerService marker,
			IConfuserComponent parentComp) {
			if (marker.IsMarked(context, def))
				return;
			// TODO: Private definitions are not properly handled there. They get a wider visibility.
			if (def is MethodDef method) {
				method.Access = MethodAttributes.Assembly;
				if (!method.IsSpecialName && !method.IsRuntimeSpecialName && !method.DeclaringType.IsDelegate())
					method.Name = RandomName();
			}
			else if (def is FieldDef field) {
				field.Access = FieldAttributes.Assembly;
				if (!field.IsSpecialName && !field.IsRuntimeSpecialName)
					field.Name = RandomName();
			}
			else if (def is TypeDef type) {
				type.Visibility = type.DeclaringType == null ? TypeAttributes.NotPublic : TypeAttributes.NestedAssembly;
				type.Namespace = "";
				if (!type.IsSpecialName && !type.IsRuntimeSpecialName)
					type.Name = RandomName();
			}

			SetCanRename(context, def, false);
			Analyze(context, def);
			marker.Mark(context, def, parentComp);
		}

		#region Charsets

		static readonly char[] asciiCharset = Enumerable.Range(32, 95)
			.Select(ord => (char)ord)
			.Except(new[] {'.'})
			.ToArray();

		static readonly char[] reflectionCharset = asciiCharset.Except(new[] { ' ', '[', ']' }).ToArray();

		static readonly char[] letterCharset = Enumerable.Range(0, 26)
			.SelectMany(ord => new[] {(char)('a' + ord), (char)('A' + ord)})
			.ToArray();

		static readonly char[] alphaNumCharset = Enumerable.Range(0, 26)
			.SelectMany(ord => new[] {(char)('a' + ord), (char)('A' + ord)})
			.Concat(Enumerable.Range(0, 10).Select(ord => (char)('0' + ord)))
			.ToArray();

		// Especially chosen, just to mess with people.
		// Inspired by: http://xkcd.com/1137/ :D
		static readonly char[] unicodeCharset = new char[] { }
			.Concat(Enumerable.Range(0x200b, 5).Select(ord => (char)ord))
			.Concat(Enumerable.Range(0x2029, 6).Select(ord => (char)ord))
			.Concat(Enumerable.Range(0x206a, 6).Select(ord => (char)ord))
			.Except(new[] {'\u2029'})
			.ToArray();

		#endregion

		public IRandomGenerator GetRandom() {
			return random;
		}

		public IList<INameReference> GetReferences(IConfuserContext context, object obj) {
			return context.Annotations.GetLazy(obj, ReferencesKey, key => new List<INameReference>());
		}

		public string GetOriginalFullName(IConfuserContext context, IDnlibDef obj) =>
			context.Annotations.Get(obj, OriginalFullNameKey, (string)null) ?? GetSimplifiedFullName(context, obj);

		public IReadOnlyDictionary<string, string> GetNameMap() => _obfuscatedToOriginalNameMap;

		public bool IsRenamed(IConfuserContext context, IDnlibDef def) => context.Annotations.Get(def, IsRenamedKey, !CanRename(context, def));

		public void SetIsRenamed(IConfuserContext context, IDnlibDef def) => context.Annotations.Set(def, IsRenamedKey, true);

		public string GetSimplifiedFullName(IConfuserContext context, IDnlibDef dnlibDef, bool forceShortNames = false) {
			string result;

			var shortNames = forceShortNames || GetParam(context, dnlibDef, _parent.Parameters.ShortNames);
			if (shortNames) {
				result = dnlibDef.Name;
			}
			else {
				if (dnlibDef is MethodDef methodDef) {
					var resultBuilder = new StringBuilder();
					resultBuilder.Append(methodDef.DeclaringType2?.FullName);
					resultBuilder.Append("::");
					resultBuilder.Append(dnlibDef.Name);

					resultBuilder.Append('(');
					if (methodDef.Signature is MethodSig methodSig) {
						var methodParams = methodSig.Params;
						for (var index = 0; index < methodParams.Count; index++) {
							resultBuilder.Append(methodParams[index]);
							if (index < methodParams.Count - 1) {
								resultBuilder.Append(',');
							}
						}
					}
					resultBuilder.Append(')');

					result = resultBuilder.ToString();
				}
				else {
					result = dnlibDef.FullName;
				}
			}

			return result;
		}
	}
}
