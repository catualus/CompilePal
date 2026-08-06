using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CompilePalX.Compilers;
using CompilePalX.Compiling;

namespace CompilePalX.Configuration
{
	static class OrderManager
	{
		public static ObservableCollection<CompileProcess> CurrentOrder;
		private static object lockObj = new object();

		public static void Init()
		{
			CurrentOrder = [];
			BindingOperations.EnableCollectionSynchronization(CurrentOrder, lockObj);
		}


		public static void UpdateOrder()
		{
			if (ConfigurationManager.CurrentPreset == null)
			{
				// Clear rather than return. Returning leaves whatever order was built for the last
				// preset in place, so a compile started with no preset selected silently runs a stale
				// step list - which looks like it worked, on a configuration that no longer exists.
				CurrentOrder.Clear();
				MainWindow.Instance.UpdateOrderGridSource(CurrentOrder);
				return;
			}

			//Get all default processes for config
			var defaultProcs = new List<CompileProcess>(ConfigurationManager.CompileProcesses
				.Where(c => c.Metadata.DoRun
					        && c.PresetDictionary.ContainsKey(ConfigurationManager.CurrentPreset)
					        && c.Name != "ORDER"
					        && c.Name != "CUSTOM"
				).ToList());

			//Get custom process
			var customProcess = (CustomProcess) ConfigurationManager.CompileProcesses
				.FirstOrDefault(c => c.Metadata.DoRun
					                    && c.PresetDictionary.ContainsKey(ConfigurationManager.CurrentPreset)
					                    && c.Name == "CUSTOM"
				);

			var newOrder = new ObservableCollection<CompileProcess>(defaultProcs);

			if (customProcess != null)
			{
				foreach (var program in customProcess.BuildProgramList().OrderBy(c => c.CustomOrder))
				{
					if (program.CustomOrder > newOrder.Count)
					{
						newOrder.Add(program);
						MainWindow.Instance.SetOrder(program, newOrder.Count - 1);
					}
					else
					{
						newOrder.Insert(program.CustomOrder, program);
					}
				}
			}

			// Worth logging even when it works: a compile whose order came out empty otherwise looks
			// like a successful compile that took no time, which is a confusing thing to debug.
			CompilePalLogger.LogLineDebug(newOrder.Count == 0
				? $"Compile order for preset '{ConfigurationManager.CurrentPreset.Name}' is EMPTY"
				: $"Compile order for preset '{ConfigurationManager.CurrentPreset.Name}': " +
				  string.Join(", ", newOrder.Select(c => c.Name)));

			//Update order
			CurrentOrder.Clear();
			CurrentOrder.AddRange(newOrder);

			MainWindow.Instance.UpdateOrderGridSource(CurrentOrder);
		}
	}
}
