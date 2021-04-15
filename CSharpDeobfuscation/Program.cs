using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace CSharpDeobfuscation
{
    internal class Program
    {
        public static void Main(string[] args)
        {
	        if (args.Length != 1) Quit("Invalid Number Of Args", -1);
	        if (!File.Exists(args[0])) Quit("Invalid file path or file not found", -1);
	        try
	        {
		        var data = File.ReadAllBytes(args[0]);
		        ModuleDef moduleDef = ModuleDefMD.Load(args[0], new ModuleContext());
		        var stats = InlineModuleMethods(moduleDef);
		        var cfStats = RemoveCF(moduleDef);
		        ModuleWriterOptions options = new ModuleWriterOptions(moduleDef);
		        options.Logger = DummyLogger.NoThrowInstance;
		        options.MetadataOptions.Flags = 0U;
		        moduleDef.Write(Directory.GetCurrentDirectory() + "\\" + moduleDef.Assembly.Name + ".Cleaned.dll", options);
		        Console.WriteLine($"Total Methods Inlined                          : {stats.Item1}");
		        Console.WriteLine($"Total Strings Decrypted                        : {stats.Item2}");
		        Console.WriteLine($"Total Control Flow Obfuscated Methods Fixed    : {cfStats.Item1}");
		        Console.WriteLine($"Total Control Flow Obfuscated Methods Failed   : {cfStats.Item2}");
		        Quit($"Finished", 1);
	        }
	        catch(Exception e)
	        {
		        Quit($"Failed to deobfuscate assembly: {e}", -1);
	        }
        }

        private static void Quit(string message, int code)
        {
	        Console.WriteLine($"{message}\nPress Enter To Close");
	        Console.ReadLine();
	        Environment.Exit(code);
        }
        public static (int, int) InlineModuleMethods(ModuleDef moduleDef)
		{
			int methodInlineCount = 0;
			int stringDecryptedCount = 0;
			foreach (var type in moduleDef.GetTypes())
			{
				foreach (var method in type.Methods)
				{
					if (method.HasBody && method.Body.HasInstructions && !method.DeclaringType.Name.Equals("<Module>"))
					{
						for (int i = 0; i < method.Body.Instructions.Count; i++)
						{
							if (method.Body.Instructions[i].OpCode == OpCodes.Call)
							{
								try
								{
									var calledMethod = (MethodDef) method.Body.Instructions[i].Operand;
									
									if (method.Body.Instructions[i].Operand.ToString().Contains("Int32 <Module>::"))
									{
										for (int j = 0; j < calledMethod.Body.Instructions.Count; j++)
										{
											if (calledMethod.Body.Instructions[j].IsLdcI4())
											{
												method.Body.Instructions[i] = Instruction.CreateLdcI4((int) calledMethod.Body.Instructions[j].Operand);
												calledMethod.DeclaringType.Methods.Remove(calledMethod);
												methodInlineCount++;
											}
										}
									}
									if (method.Body.Instructions[i].Operand.ToString().StartsWith("System.String <Module>::k")) 
									{
										//Sometimes the string method calls before are inlined weirdly enough, easy enough fix anyway
										if (method.Body.Instructions[i - 1].OpCode == OpCodes.Ldstr)
										{
											method.Body.Instructions[i].OpCode = OpCodes.Ldstr;
											method.Body.Instructions[i].Operand = DecryptFunction((string) method.Body.Instructions[i - 1].Operand);
											method.Body.Instructions[i - 1].OpCode = OpCodes.Nop;
											stringDecryptedCount++;
										}
										else
										{
											calledMethod = (MethodDef) method.Body.Instructions[i - 1].Operand;
											for (int j = 0; j < calledMethod.Body.Instructions.Count; j++)
											{
												if (calledMethod.Body.Instructions[j].OpCode == OpCodes.Ldstr)
												{
													method.Body.Instructions[i].OpCode = OpCodes.Ldstr;
													method.Body.Instructions[i].Operand = DecryptFunction((string) calledMethod.Body.Instructions[j].Operand);
													method.Body.Instructions[i - 1].OpCode = OpCodes.Nop;
													calledMethod.DeclaringType.Methods.Remove(calledMethod);
													stringDecryptedCount++;
													methodInlineCount++;
												}
											}
										}
									}
								}
								catch
								{
									
								}
							}
						}
					}
				}
			}
			return (methodInlineCount, stringDecryptedCount);
		}
		private static string DecryptFunction(string input)
		{
			StringBuilder stringBuilder = new StringBuilder();
			foreach (string text in input.Split('A'))
			{
				stringBuilder.Append(Convert.ToChar(text.Length).ToString() ?? "");
			}
			return Encoding.UTF8.GetString(Convert.FromBase64String(stringBuilder.ToString().Substring(0, stringBuilder.ToString().Length - 1))); 
		}	
		
		public static (int, int) RemoveCF(ModuleDef moduleDef)
		{
			int success = 0;
			int failed = 0;
			foreach (TypeDef type in moduleDef.GetTypes())
			{
				foreach (MethodDef method in type.Methods)
				{
					if (method.HasBody && HasCF(method))
					{
						try
						{
							var cflowDeobfuscator = new BlocksCflowDeobfuscator();
							Blocks blocks = new Blocks(method);
							List<Block> test = blocks.MethodBlocks.GetAllBlocks();
							blocks.RemoveDeadBlocks();
							blocks.RepartitionBlocks();
							blocks.UpdateBlocks();
							blocks.Method.Body.OptimizeBranches();
							blocks.UpdateBlocks();
							blocks.Method.Body.SimplifyBranches(); 
							blocks.UpdateBlocks();
							cflowDeobfuscator.Initialize(blocks);
							cflowDeobfuscator.Add(new ControlFlow_BlockDeobfuscator());
							cflowDeobfuscator.Deobfuscate();
							blocks.RepartitionBlocks();
							IList<Instruction> instructions;
							IList<ExceptionHandler> exceptionHandlers;
							blocks.GetCode(out instructions, out exceptionHandlers);
							DotNetUtils.RestoreBody(method, instructions, exceptionHandlers);
							success++;
						}
						catch(Exception e)
						{
							Console.WriteLine($"Failed to deobfuscate {method.DeclaringType.Name}:{method.Name} with exception: {e.Message}");
							failed++;
						}
					}
				}
			}
			return (success, failed);
		}
		
		private static bool HasCF(MethodDef method)
		{
			for (int i = 0; i < method.Body.Instructions.Count; i++)
			{
				if (method.Body.Instructions[i].OpCode == OpCodes.Switch) return true;
			}
			return false;
		}
		
		private class ControlFlow_BlockDeobfuscator : BlockDeobfuscator
		{
			protected override bool Deobfuscate(Block block)
			{
				return false;
			}
			public InstructionEmulator InstructionEmulator = new InstructionEmulator();
		}
    }
}
