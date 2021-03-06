﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Confuser.Core;
using Confuser.Core.Services;
using Confuser.DynCipher;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Writer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Confuser.Protections.ControlFlow {
	internal class ControlFlowPhase : IProtectionPhase {
		static readonly JumpMangler Jump = new JumpMangler();
		static readonly SwitchMangler Switch = new SwitchMangler();

		public ControlFlowPhase(ControlFlowProtection parent) =>
			Parent = parent ?? throw new ArgumentNullException(nameof(parent));

		public ControlFlowProtection Parent { get; }

		IConfuserComponent IProtectionPhase.Parent => Parent;

		public ProtectionTargets Targets => ProtectionTargets.Methods;

		public bool ProcessAll => false;

		public string Name => "Control flow mangling";

		CFContext ParseParameters(MethodDef method, IConfuserContext context, IProtectionParameters parameters,
			IRandomGenerator random, bool disableOpti) {
			var ret = new CFContext();
			ret.Type = parameters.GetParameter(context, method, Parent.Parameters.ControlFlowType);
			ret.Predicate = parameters.GetParameter(context, method, Parent.Parameters.PredicateType);
			ret.Intensity = parameters.GetParameter(context, method, Parent.Parameters.Intensity);
			ret.Depth = parameters.GetParameter(context, method, Parent.Parameters.Depth);

			ret.JunkCode = parameters.GetParameter(context, method, Parent.Parameters.AddJunkCode) && !disableOpti;

			ret.Protection = (ControlFlowProtection)Parent;
			ret.Random = random;
			ret.Method = method;
			ret.Context = context;
			ret.DynCipher = context.Registry.GetRequiredService<IDynCipherService>();

			if (ret.Predicate == PredicateType.x86) {
				if ((context.CurrentModule.Cor20HeaderFlags & ComImageFlags.ILOnly) != 0)
					context.CurrentModuleWriterOptions.Cor20HeaderOptions.Flags &= ~ComImageFlags.ILOnly;
			}

			return ret;
		}

		static bool DisabledOptimization(ModuleDef module) {
			bool disableOpti = false;
			CustomAttribute debugAttr = module.Assembly.CustomAttributes.Find("System.Diagnostics.DebuggableAttribute");
			if (debugAttr != null) {
				if (debugAttr.ConstructorArguments.Count == 1)
					disableOpti |= ((DebuggableAttribute.DebuggingModes)(int)debugAttr.ConstructorArguments[0].Value &
					                DebuggableAttribute.DebuggingModes.DisableOptimizations) != 0;
				else
					disableOpti |= (bool)debugAttr.ConstructorArguments[1].Value;
			}

			debugAttr = module.CustomAttributes.Find("System.Diagnostics.DebuggableAttribute");
			if (debugAttr != null) {
				if (debugAttr.ConstructorArguments.Count == 1)
					disableOpti |= ((DebuggableAttribute.DebuggingModes)(int)debugAttr.ConstructorArguments[0].Value &
					                DebuggableAttribute.DebuggingModes.DisableOptimizations) != 0;
				else
					disableOpti |= (bool)debugAttr.ConstructorArguments[1].Value;
			}

			return disableOpti;
		}

		void IProtectionPhase.Execute(IConfuserContext context, IProtectionParameters parameters,
			CancellationToken token) {
			bool disabledOpti = DisabledOptimization(context.CurrentModule);
			var random = context.Registry.GetRequiredService<IRandomService>()
				.GetRandomGenerator(ControlFlowProtection._FullId);
			var logger = context.Registry.GetRequiredService<ILoggerFactory>().CreateLogger(ControlFlowProtection._Id);

			foreach (var method in parameters.Targets.OfType<MethodDef>()) //.WithProgress(logger))
				if (method.HasBody && method.Body.Instructions.Count > 0) {
					ProcessMethod(method, ParseParameters(method, context, parameters, random, disabledOpti));
					token.ThrowIfCancellationRequested();
				}
		}

		static ManglerBase GetMangler(CFType type) {
			if (type == CFType.Switch)
				return Switch;
			return Jump;
		}

		void ProcessMethod(MethodDef methodDef, CFContext ctx) {
			var body = methodDef.Body;
			if (!MaxStackCalculator.GetMaxStack(body.Instructions, body.ExceptionHandlers, out var maxStack)) {
				var logger = ctx.Context.Registry.GetRequiredService<ILoggerFactory>()
					.CreateLogger(ControlFlowProtection._Id);
				logger.LogCritical("Failed to calculate maxstack.");
				throw new ConfuserException();
			}

			body.MaxStack = (ushort)maxStack;
			ScopeBlock root = BlockParser.ParseBody(body);

			GetMangler(ctx.Type).Mangle(body, root, ctx);

			body.Instructions.Clear();
			root.ToBody(body);
			if (body.PdbMethod != null) {
				body.PdbMethod = new PdbMethod() {
					Scope = new PdbScope() {
						Start = body.Instructions.First(),
						End = body.Instructions.Last()
					}
				};
			}

			methodDef.CustomDebugInfos.RemoveWhere(cdi => cdi is PdbStateMachineHoistedLocalScopesCustomDebugInfo);

			foreach (ExceptionHandler eh in body.ExceptionHandlers) {
				var index = body.Instructions.IndexOf(eh.TryEnd) + 1;
				eh.TryEnd = index < body.Instructions.Count ? body.Instructions[index] : null;
				index = body.Instructions.IndexOf(eh.HandlerEnd) + 1;
				eh.HandlerEnd = index < body.Instructions.Count ? body.Instructions[index] : null;
			}

			body.KeepOldMaxStack = true;
		}
	}
}
