﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Confuser.Helpers {
	public static partial class InjectHelper {
		/// <summary>
		///     The injector actually does the injecting.
		/// </summary>
		private sealed class Injector : ImportMapper {
			private readonly Dictionary<IMemberDef, IMemberDef> _injectedMembers;

			private static readonly ImmutableList<IMethodInjectProcessor> _defaultProcessors =
				ImmutableList.Create<IMethodInjectProcessor>(new UnsafeMemoryProcessor());

			private InjectContext InjectContext { get; }

			private IInjectBehavior InjectBehavior { get; }

			internal IReadOnlyDictionary<IMemberDef, IMemberDef> InjectedMembers => _injectedMembers;

			private Queue<IMemberDef> PendingForInject { get; }

			private IImmutableList<IMethodInjectProcessor> MethodInjectProcessors { get; }

			internal Injector(InjectContext injectContext, IInjectBehavior injectBehavior,
				IEnumerable<IMethodInjectProcessor> injectProcessors) {
				InjectContext = injectContext ?? throw new ArgumentNullException(nameof(injectContext));
				InjectBehavior = injectBehavior ?? throw new ArgumentNullException(nameof(injectBehavior));
				PendingForInject = new Queue<IMemberDef>();
				MethodInjectProcessors = _defaultProcessors.AddRange(injectProcessors ?? Enumerable.Empty<IMethodInjectProcessor>());
				_injectedMembers = new Dictionary<IMemberDef, IMemberDef>();
			}

			private TypeDefUser CopyDef(TypeDef source) {
				Debug.Assert(source != null, $"{nameof(source)} != null");

				if (_injectedMembers.TryGetValue(source, out var importedMember))
					return (TypeDefUser)importedMember;

				var typeDefUser = new TypeDefUser(source.Namespace, source.Name) {
					Attributes = source.Attributes
				};

				if (source.HasClassLayout)
					typeDefUser.ClassLayout = new ClassLayoutUser(source.ClassLayout.PackingSize, source.ClassSize);

				CloneGenericParameters(source, typeDefUser);

				_injectedMembers.Add(source, typeDefUser);
				PendingForInject.Enqueue(source);
				if (source.IsDelegate)
					foreach (var m in source.Methods)
						PendingForInject.Enqueue(m);

				if (source.IsEnum) {
					// The backing value field of a enum is required.
					foreach (var valueField in source.Fields.Where(f => !f.IsStatic))
						PendingForInject.Enqueue(valueField);
				}

				return typeDefUser;
			}

			private MethodDefUser CopyDef(MethodDef source) {
				Debug.Assert(source != null, $"{nameof(source)} != null");

				if (_injectedMembers.TryGetValue(source, out var importedMember))
					return (MethodDefUser)importedMember;

				var methodDefUser = new MethodDefUser(source.Name, null, source.ImplAttributes, source.Attributes) {
					Attributes = source.Attributes
				};

				CloneGenericParameters(source, methodDefUser);

				_injectedMembers.Add(source, methodDefUser);
				PendingForInject.Enqueue(source);

				return methodDefUser;
			}

			private FieldDefUser CopyDef(FieldDef source) {
				Debug.Assert(source != null, $"{nameof(source)} != null");

				if (_injectedMembers.TryGetValue(source, out var importedMember))
					return (FieldDefUser)importedMember;

				var fieldDefUser = new FieldDefUser(source.Name, null, source.Attributes);

				_injectedMembers.Add(source, fieldDefUser);
				PendingForInject.Enqueue(source);

				return fieldDefUser;
			}

			private EventDefUser CopyDef(EventDef source) {
				Debug.Assert(source != null, $"{nameof(source)} != null");

				if (_injectedMembers.TryGetValue(source, out var importedMember))
					return (EventDefUser)importedMember;

				var eventDefUser = new EventDefUser(source.Name, null, source.Attributes);

				_injectedMembers.Add(source, eventDefUser);
				PendingForInject.Enqueue(source);

				return eventDefUser;
			}

			private PropertyDefUser CopyDef(PropertyDef source) {
				Debug.Assert(source != null, $"{nameof(source)} != null");

				if (_injectedMembers.TryGetValue(source, out var importedMember))
					return (PropertyDefUser)importedMember;

				var propertyDefUser = new PropertyDefUser(source.Name, null, source.Attributes);

				_injectedMembers.Add(source, propertyDefUser);
				PendingForInject.Enqueue(source);

				return propertyDefUser;
			}

			private static void CloneGenericParameters(ITypeOrMethodDef origin, ITypeOrMethodDef result) {
				if (origin.HasGenericParameters)
					foreach (var genericParam in origin.GenericParameters)
						result.GenericParameters.Add(new GenericParamUser(genericParam.Number, genericParam.Flags,
							"-"));
			}

			private IReadOnlyCollection<IMemberDef> InjectRemaining(Importer importer,
				IImmutableList<IMethodInjectProcessor> methodInjectProcessors) {
				var resultBuilder = ImmutableList.CreateBuilder<IMemberDef>();

				while (PendingForInject.Count > 0) {
					var memberDef = PendingForInject.Dequeue();
					if (memberDef is TypeDef typeDef)
						resultBuilder.Add(InjectTypeDef(typeDef, importer));
					else if (memberDef is MethodDef methodDef)
						resultBuilder.Add(InjectMethodDef(methodDef, importer, methodInjectProcessors));
					else if (memberDef is FieldDef fieldDef)
						resultBuilder.Add(InjectFieldDef(fieldDef, importer));
					else if (memberDef is EventDef eventDef)
						resultBuilder.Add(InjectEventDef(eventDef, importer));
					else if (memberDef is PropertyDef propertyDef)
						resultBuilder.Add(InjectPropertyDef(propertyDef, importer));
					else
						Debug.Fail("Unexpected member in remaining import list:" + memberDef.GetType().Name);
				}

				return resultBuilder.ToImmutable();
			}

			internal MethodDef Inject(MethodDef methodDef) {
				var existingMappedMethodDef = InjectContext.ResolveMapped(methodDef);
				if (existingMappedMethodDef != null) return existingMappedMethodDef;

				var importer = new Importer(InjectContext.TargetModule, ImporterOptions.TryToUseDefs,
					new GenericParamContext(), this);
				var methodInjectProcessors = MethodInjectProcessors.Add(new ImportProcessor(importer));
				var result = InjectMethodDef(methodDef, importer, methodInjectProcessors);
				InjectRemaining(importer, methodInjectProcessors);
				return result;
			}

			internal TypeDef Inject(TypeDef typeDef) {
				var existingMappedTypeDef = InjectContext.ResolveMapped(typeDef);
				if (existingMappedTypeDef != null) return existingMappedTypeDef;

				var importer = new Importer(InjectContext.TargetModule, ImporterOptions.TryToUseDefs,
					new GenericParamContext(), this);
				var methodInjectProcessors = MethodInjectProcessors.Add(new ImportProcessor(importer));
				var result = InjectTypeDef(typeDef, importer);
				foreach (var method in typeDef.Methods) CopyDef(method);
				foreach (var field in typeDef.Fields) CopyDef(field);
				foreach (var @event in typeDef.Events) CopyDef(@event);
				foreach (var prop in typeDef.Properties) CopyDef(prop);
				foreach (var nestedType in typeDef.NestedTypes) Inject(nestedType);

				InjectRemaining(importer, methodInjectProcessors);
				return result;
			}

			private TypeDef InjectTypeDef(TypeDef typeDef, Importer importer) {
				if (typeDef == null) throw new ArgumentNullException(nameof(typeDef));

				var existingTypeDef = InjectContext.ResolveMapped(typeDef);
				if (existingTypeDef != null) return existingTypeDef;

				var newTypeDef = CopyDef(typeDef);
				newTypeDef.BaseType = importer.Import(typeDef.BaseType);

				if (typeDef.DeclaringType != null)
					newTypeDef.DeclaringType = InjectTypeDef(typeDef.DeclaringType, importer);

				foreach (var iface in typeDef.Interfaces)
					newTypeDef.Interfaces.Add(InjectInterfaceImpl(iface, importer));

				InjectCustomAttributes(typeDef, newTypeDef, importer);

				InjectBehavior.Process(typeDef, newTypeDef, importer);

				if (!newTypeDef.IsNested)
					InjectContext.TargetModule.Types.Add(newTypeDef);

				InjectContext.TargetModule.UpdateRowId(newTypeDef);
				InjectContext.ApplyMapping(typeDef, newTypeDef);

				var defaultConstructor = typeDef.FindDefaultConstructor();
				if (defaultConstructor != null)
					PendingForInject.Enqueue(defaultConstructor);

				var staticConstructor = typeDef.FindStaticConstructor();
				if (staticConstructor != null)
					PendingForInject.Enqueue(staticConstructor);

				return newTypeDef;
			}

			private InterfaceImplUser InjectInterfaceImpl(InterfaceImpl interfaceImpl, Importer importer) {
				if (interfaceImpl == null) throw new ArgumentNullException(nameof(interfaceImpl));

				var typeDefOrRef = importer.Import(interfaceImpl.Interface);
				if (!(typeDefOrRef is TypeDef typeDef))
					typeDef = ((TypeRef)typeDefOrRef).Resolve();
				if (typeDef != null && !typeDef.IsInterface)
					throw new InvalidOperationException("Type for Interface is not a interface?!");

				var resultImpl = new InterfaceImplUser(typeDefOrRef);
				InjectContext.TargetModule.UpdateRowId(resultImpl);
				return resultImpl;
			}

			private static void InjectCustomAttributes(IHasCustomAttribute source, IHasCustomAttribute target, Importer importer) {
				foreach (var ca in source.CustomAttributes) {
					// Nobody needs to know about suppressed messages in the runtime code!
					if (ca.TypeFullName.Equals(typeof(SuppressMessageAttribute).FullName, StringComparison.Ordinal))
						continue;

					target.CustomAttributes.Add(InjectCustomAttribute(ca, importer));
				}
			}

			private static CustomAttribute InjectCustomAttribute(CustomAttribute attribute, Importer importer) {
				Debug.Assert(attribute != null, $"{nameof(attribute)} != null");

				var result = new CustomAttribute((ICustomAttributeType)importer.Import(attribute.Constructor));
				foreach (var arg in attribute.ConstructorArguments)
					result.ConstructorArguments.Add(new CAArgument(importer.Import(arg.Type), arg.Value));

				foreach (var arg in attribute.NamedArguments)
					result.NamedArguments.Add(
						new CANamedArgument(arg.IsField, importer.Import(arg.Type), arg.Name,
							new CAArgument(importer.Import(arg.Argument.Type), arg.Argument.Value)));

				return result;
			}

			private FieldDef InjectFieldDef(FieldDef fieldDef, Importer importer) {
				Debug.Assert(fieldDef != null, $"{nameof(fieldDef)} != null");

				var existingFieldDef = InjectContext.ResolveMapped(fieldDef);
				if (existingFieldDef != null) return existingFieldDef;

				var newFieldDef = CopyDef(fieldDef);
				newFieldDef.Signature = importer.Import(fieldDef.Signature);
				newFieldDef.DeclaringType = (TypeDef)importer.Import(fieldDef.DeclaringType);
				newFieldDef.InitialValue = fieldDef.InitialValue;

				if (newFieldDef.HasFieldRVA)
					newFieldDef.RVA = fieldDef.RVA;
				
				InjectCustomAttributes(fieldDef, newFieldDef, importer);

				InjectBehavior.Process(fieldDef, newFieldDef, importer);
				InjectContext.TargetModule.UpdateRowId(newFieldDef);
				InjectContext.ApplyMapping(fieldDef, newFieldDef);

				return newFieldDef;
			}

			private MethodDef InjectMethodDef(MethodDef methodDef, Importer importer,
				IEnumerable<IMethodInjectProcessor> methodInjectProcessors) {
				Debug.Assert(methodDef != null, $"{nameof(methodDef)} != null");
				Debug.Assert(methodInjectProcessors != null, $"{nameof(methodInjectProcessors)} != null");

				var existingMethodDef = InjectContext.ResolveMapped(methodDef);
				if (existingMethodDef != null) return existingMethodDef;

				var newMethodDef = CopyDef(methodDef);
				newMethodDef.DeclaringType = (TypeDef)importer.Import(methodDef.DeclaringType);
				newMethodDef.Signature = importer.Import(methodDef.Signature);
				newMethodDef.Parameters.UpdateParameterTypes();
				
				foreach (var paramDef in methodDef.ParamDefs)
					newMethodDef.ParamDefs.Add(new ParamDefUser(paramDef.Name, paramDef.Sequence, paramDef.Attributes));

				if (methodDef.ImplMap != null)
					newMethodDef.ImplMap =
						new ImplMapUser(new ModuleRefUser(InjectContext.TargetModule, methodDef.ImplMap.Module.Name),
							methodDef.ImplMap.Name, methodDef.ImplMap.Attributes);
				
				InjectCustomAttributes(methodDef, newMethodDef, importer);

				if (methodDef.HasBody) {
					methodDef.Body.SimplifyBranches();
					methodDef.Body.SimplifyMacros(methodDef.Parameters);

					newMethodDef.Body = new CilBody(methodDef.Body.InitLocals, new List<Instruction>(),
						new List<ExceptionHandler>(), new List<Local>());
					newMethodDef.Body.MaxStack = methodDef.Body.MaxStack;

					var bodyMap = new Dictionary<object, object>();

					foreach (var local in methodDef.Body.Variables) {
						var newLocal = new Local(importer.Import(local.Type));
						newMethodDef.Body.Variables.Add(newLocal);
						newLocal.Name = local.Name;

						bodyMap[local] = newLocal;
					}

					foreach (var instr in methodDef.Body.Instructions) {
						var newInstr = new Instruction(instr.OpCode, instr.Operand) {
							SequencePoint = instr.SequencePoint
						};

						newMethodDef.Body.Instructions.Add(newInstr);
						bodyMap[instr] = newInstr;
					}

					foreach (var instr in newMethodDef.Body.Instructions) {
						if (instr.Operand != null && bodyMap.ContainsKey(instr.Operand))
							instr.Operand = bodyMap[instr.Operand];

						else if (instr.Operand is Instruction[] instructionArrayOp)
							instr.Operand = (instructionArrayOp).Select(target => (Instruction)bodyMap[target])
								.ToArray();
					}

					foreach (var eh in methodDef.Body.ExceptionHandlers)
						newMethodDef.Body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType) {
							CatchType = eh.CatchType == null ? null : importer.Import(eh.CatchType),
							TryStart = (Instruction)bodyMap[eh.TryStart],
							TryEnd = (Instruction)bodyMap[eh.TryEnd],
							HandlerStart = (Instruction)bodyMap[eh.HandlerStart],
							HandlerEnd = (Instruction)bodyMap[eh.HandlerEnd],
							FilterStart = eh.FilterStart == null ? null : (Instruction)bodyMap[eh.FilterStart]
						});

					foreach (var processor in methodInjectProcessors)
						processor.Process(newMethodDef);
				}

				InjectBehavior.Process(methodDef, newMethodDef, importer);
				InjectContext.TargetModule.UpdateRowId(newMethodDef);
				InjectContext.ApplyMapping(methodDef, newMethodDef);

				return newMethodDef;
			}

			private EventDef InjectEventDef(EventDef eventDef, Importer importer) {
				Debug.Assert(eventDef != null, $"{nameof(eventDef)} != null");

				var existingEventDef = InjectContext.ResolveMapped(eventDef);
				if (existingEventDef != null) return existingEventDef;

				var newEventDef = CopyDef(eventDef);
				newEventDef.AddMethod = CopyDef(eventDef.AddMethod);
				newEventDef.InvokeMethod = CopyDef(eventDef.InvokeMethod);
				newEventDef.RemoveMethod = CopyDef(eventDef.RemoveMethod);
				if (eventDef.HasOtherMethods) {
					foreach (var otherMethod in eventDef.OtherMethods)
						newEventDef.OtherMethods.Add(CopyDef(otherMethod));
				}

				newEventDef.DeclaringType = (TypeDef)importer.Import(eventDef.DeclaringType);
				
				InjectCustomAttributes(eventDef, newEventDef, importer);

				InjectBehavior.Process(eventDef, newEventDef, importer);
				InjectContext.TargetModule.UpdateRowId(newEventDef);
				InjectContext.ApplyMapping(eventDef, newEventDef);

				return newEventDef;
			}

			private PropertyDef InjectPropertyDef(PropertyDef propertyDef, Importer importer) {
				Debug.Assert(propertyDef != null, $"{nameof(propertyDef)} != null");

				var existingPropertyDef = InjectContext.ResolveMapped(propertyDef);
				if (existingPropertyDef != null) return existingPropertyDef;

				var newPropertyDef = CopyDef(propertyDef);
				foreach (var getMethod in propertyDef.GetMethods)
					newPropertyDef.GetMethods.Add(CopyDef(getMethod));
				foreach (var setMethod in propertyDef.SetMethods)
					newPropertyDef.SetMethods.Add(CopyDef(setMethod));

				if (propertyDef.HasOtherMethods) {
					foreach (var otherMethod in propertyDef.OtherMethods)
						newPropertyDef.OtherMethods.Add(CopyDef(otherMethod));
				}

				newPropertyDef.DeclaringType = (TypeDef)importer.Import(propertyDef.DeclaringType);
				
				InjectCustomAttributes(propertyDef, newPropertyDef, importer);

				InjectBehavior.Process(propertyDef, newPropertyDef, importer);
				InjectContext.TargetModule.UpdateRowId(newPropertyDef);
				InjectContext.ApplyMapping(propertyDef, newPropertyDef);

				return newPropertyDef;
			}

			#region ImportMapper

			public override ITypeDefOrRef Map(ITypeDefOrRef typeDefOrRef) {
				if (typeDefOrRef is TypeDef typeDef) {
					var mappedType = InjectContext.ResolveMapped(typeDef);
					if (mappedType != null) return mappedType;

					if (typeDef.Module == InjectContext.OriginModule)
						return CopyDef(typeDef);
				}

				// check if the assembly reference needs to be fixed.
				if (typeDefOrRef is TypeRef sourceRef) {
					var targetAssemblyRef = InjectContext.TargetModule.GetAssemblyRef(sourceRef.DefinitionAssembly.Name);
					if (!(targetAssemblyRef is null) && !string.Equals(targetAssemblyRef.FullName, typeDefOrRef.DefinitionAssembly.FullName, StringComparison.Ordinal)) {
						// We got a matching assembly by the simple name, but not by the full name.
						// This means the injected code uses a different assembly version than the target assembly.
						// We'll fix the assembly reference, to avoid breaking anything.
						return new TypeRefUser(sourceRef.Module, sourceRef.Namespace, sourceRef.Name, targetAssemblyRef);
					}
				}

				return base.Map(typeDefOrRef);
			}

			public override IMethod Map(MethodDef methodDef) {
				var mappedMethod = InjectContext.ResolveMapped(methodDef);
				if (mappedMethod != null) return mappedMethod;

				if (methodDef.Module == InjectContext.OriginModule)
					return CopyDef(methodDef);
				return base.Map(methodDef);
			}

			public override IField Map(FieldDef fieldDef) {
				var mappedField = InjectContext.ResolveMapped(fieldDef);
				if (mappedField != null) return mappedField;

				if (fieldDef.Module == InjectContext.OriginModule)
					return CopyDef(fieldDef);
				return base.Map(fieldDef);
			}

			#endregion

			private struct ImportProcessor : IMethodInjectProcessor {
				private Importer _importer;

				internal ImportProcessor(Importer importer) => _importer = importer;

				void IMethodInjectProcessor.Process(MethodDef method) {
					Debug.Assert(method != null, $"{nameof(method)} != null");

					if (method.HasBody && method.Body.HasInstructions)
						foreach (var instruction in method.Body.Instructions) {
							if (instruction.Operand is IType typeOp) {
								var importedType = _importer.Import(typeOp);
								Debug.Assert(importedType != null, $"{nameof(importedType)} != null");
								instruction.Operand = importedType;
							}
							else if (instruction.Operand is IMethod methodOp) {
								var importedMethod = _importer.Import(methodOp);
								Debug.Assert(importedMethod != null, $"{nameof(importedMethod)} != null");
								instruction.Operand = importedMethod;
							}
							else if (instruction.Operand is IField fieldOp) {
								var importedField = _importer.Import(fieldOp);
								Debug.Assert(importedField != null, $"{nameof(importedField)} != null");
								instruction.Operand = importedField;
							}
						}
				}
			}
		}
	}
}
