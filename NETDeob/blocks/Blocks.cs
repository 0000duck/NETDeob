/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace de4dot.blocks {
	public class Blocks {
		MethodDefinition method;
		IList<VariableDefinition> locals;
		MethodBlocks methodBlocks;

		public MethodBlocks MethodBlocks {
			get { return methodBlocks; }
		}

		public IList<VariableDefinition> Locals {
			get { return locals; }
		}

		public MethodDefinition Method {
			get { return method; }
		}

		public Blocks(MethodDefinition method) {
			this.method = method;
			updateBlocks();
		}

		public void updateBlocks() {
			var body = method.Body;
			locals = body.Variables;
			methodBlocks = new InstructionListParser(body.Instructions, body.ExceptionHandlers).parse();
		}

		IEnumerable<ScopeBlock> getAllScopeBlocks(ScopeBlock scopeBlock) {
			var list = new List<ScopeBlock>();
			list.Add(scopeBlock);
			list.AddRange(scopeBlock.getAllScopeBlocks());
			return list;
		}

		public int removeDeadBlocks() {
			return new DeadBlocksRemover(methodBlocks).remove();
		}

		public void getCode(out IList<Instruction> allInstructions, out IList<ExceptionHandler> allExceptionHandlers) {
			new CodeGenerator(methodBlocks).getCode(out allInstructions, out allExceptionHandlers);
		}

		struct LocalVariableInfo {
			public Block block;
			public int index;
			public LocalVariableInfo(Block block, int index) {
				this.block = block;
				this.index = index;
			}
		}

		public int optimizeLocals() {
			if (locals.Count == 0)
				return 0;

			var usedLocals = new Dictionary<VariableDefinition, List<LocalVariableInfo>>();
			foreach (var block in methodBlocks.getAllBlocks()) {
				for (int i = 0; i < block.Instructions.Count; i++) {
					var instr = block.Instructions[i];
					VariableDefinition local;
					switch (instr.OpCode.Code) {
					case Code.Ldloc:
					case Code.Ldloc_S:
					case Code.Ldloc_0:
					case Code.Ldloc_1:
					case Code.Ldloc_2:
					case Code.Ldloc_3:
					case Code.Stloc:
					case Code.Stloc_S:
					case Code.Stloc_0:
					case Code.Stloc_1:
					case Code.Stloc_2:
					case Code.Stloc_3:
						local = Instr.getLocalVar(locals, instr);
						break;

					case Code.Ldloca_S:
					case Code.Ldloca:
						local = (VariableDefinition)instr.Operand;
						break;

					default:
						local = null;
						break;
					}
					if (local == null)
						continue;

					List<LocalVariableInfo> list;
					if (!usedLocals.TryGetValue(local, out list))
						usedLocals[local] = list = new List<LocalVariableInfo>();
					list.Add(new LocalVariableInfo(block, i));
					if (usedLocals.Count == locals.Count)
						return 0;
				}
			}

			int newIndex = -1;
			var newLocals = new List<VariableDefinition>(usedLocals.Count);
			foreach (var local in usedLocals.Keys) {
				newIndex++;
				newLocals.Add(local);
				foreach (var info in usedLocals[local])
					info.block.Instructions[info.index] = new Instr(optimizeLocalInstr(info.block.Instructions[info.index], local, (uint)newIndex));
			}

			int numRemoved = locals.Count - newLocals.Count;
			locals.Clear();
			foreach (var local in newLocals)
				locals.Add(local);
			return numRemoved;
		}

		static Instruction optimizeLocalInstr(Instr instr, VariableDefinition local, uint newIndex) {
			switch (instr.OpCode.Code) {
			case Code.Ldloc:
			case Code.Ldloc_S:
			case Code.Ldloc_0:
			case Code.Ldloc_1:
			case Code.Ldloc_2:
			case Code.Ldloc_3:
				if (newIndex == 0)
					return Instruction.Create(OpCodes.Ldloc_0);
				if (newIndex == 1)
					return Instruction.Create(OpCodes.Ldloc_1);
				if (newIndex == 2)
					return Instruction.Create(OpCodes.Ldloc_2);
				if (newIndex == 3)
					return Instruction.Create(OpCodes.Ldloc_3);
				if (newIndex <= 0xFF)
					return Instruction.Create(OpCodes.Ldloc_S, local);
				return Instruction.Create(OpCodes.Ldloc, local);

			case Code.Stloc:
			case Code.Stloc_S:
			case Code.Stloc_0:
			case Code.Stloc_1:
			case Code.Stloc_2:
			case Code.Stloc_3:
				if (newIndex == 0)
					return Instruction.Create(OpCodes.Stloc_0);
				if (newIndex == 1)
					return Instruction.Create(OpCodes.Stloc_1);
				if (newIndex == 2)
					return Instruction.Create(OpCodes.Stloc_2);
				if (newIndex == 3)
					return Instruction.Create(OpCodes.Stloc_3);
				if (newIndex <= 0xFF)
					return Instruction.Create(OpCodes.Stloc_S, local);
				return Instruction.Create(OpCodes.Stloc, local);

			case Code.Ldloca_S:
			case Code.Ldloca:
				if (newIndex <= 0xFF)
					return Instruction.Create(OpCodes.Ldloca_S, local);
				return Instruction.Create(OpCodes.Ldloca, local);

			default:
				throw new ApplicationException("Invalid ld/st local instruction");
			}
		}

		public void repartitionBlocks() {
			mergeNopBlocks();
			foreach (var scopeBlock in getAllScopeBlocks(methodBlocks)) {
				try {
					scopeBlock.repartitionBlocks();
				}
				catch (NullReferenceException) {
					//TODO: Send this message to the log
					Console.WriteLine("Null ref exception! Invalid metadata token in code? Method: {0:X8}: {1}", method.MetadataToken.ToUInt32(), method.FullName);
					return;
				}
			}
		}

		void mergeNopBlocks() {
			var allBlocks = methodBlocks.getAllBlocks();

			var nopBlocks = new Dictionary<Block, bool>();
			foreach (var nopBlock in allBlocks) {
				if (nopBlock.isNopBlock())
					nopBlocks[nopBlock] = true;
			}

			if (nopBlocks.Count == 0)
				return;

			for (int i = 0; i < 10; i++) {
				bool changed = false;

				foreach (var block in allBlocks) {
					Block nopBlockTarget;

					nopBlockTarget = getNopBlockTarget(nopBlocks, block, block.FallThrough);
					if (nopBlockTarget != null) {
						block.setNewFallThrough(nopBlockTarget);
						changed = true;
					}

					if (block.Targets != null) {
						for (int targetIndex = 0; targetIndex < block.Targets.Count; targetIndex++) {
							nopBlockTarget = getNopBlockTarget(nopBlocks, block, block.Targets[targetIndex]);
							if (nopBlockTarget == null)
								continue;
							block.setNewTarget(targetIndex, nopBlockTarget);
							changed = true;
						}
					}
				}

				if (!changed)
					break;
			}

			foreach (var nopBlock in nopBlocks.Keys)
				nopBlock.Parent.removeDeadBlock(nopBlock);
		}

		static Block getNopBlockTarget(Dictionary<Block, bool> nopBlocks, Block source, Block nopBlock) {
			if (nopBlock == null || !nopBlocks.ContainsKey(nopBlock) || source == nopBlock.FallThrough)
				return null;
			if (nopBlock.Parent.BaseBlocks[0] == nopBlock)
				return null;
			var target = nopBlock.FallThrough;
			if (nopBlock.Parent != target.Parent)
				return null;
			return target;
		}
	}
}
