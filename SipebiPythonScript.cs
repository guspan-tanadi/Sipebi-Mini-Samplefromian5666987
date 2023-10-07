﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IronPython;
using IronPython.Hosting;
using IronPython.Runtime;
using Microsoft.Scripting.Hosting;
using SipebiMini.Core;

namespace SipebiMini {
	//IronPython example is taken from:
	//https://www.dotnetlovers.com/article/216/executing-python-script-from-c-sharp
	public class SipebiPythonScript {
		//All properties of a script
		private string pyScriptPath = string.Empty;
		private ScriptEngine pyEngine;
		private string pyScript;
		private ScriptSource pySource;
		private ScriptScope pyScope;
		private dynamic pyClass;
		public dynamic PyInstance;

		public bool IsReady { get; private set; } = false;
		public bool HasSharedResources { get; private set; }
		public List<string> SharedResourcesInputKeys { get; private set; } = new List<string>();
		public List<string> SharedResourcesOutputKeys { get; private set; } = new List<string>();
		public List<string> FileResourceNames { get; private set; } = new List<string>();

		public void Initialize(ScriptEngine pyEngine, string scriptPath, string scriptFileName, string scriptClassName) {
			if (pyEngine == null || string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath) ||
				string.IsNullOrWhiteSpace(scriptFileName) || string.IsNullOrWhiteSpace(scriptClassName))
				return;
			//Get the script, prepare all necessary properties associated with the script
			pyScriptPath = scriptPath;
			pyScript = getFormattedScript(scriptPath);
			if (string.IsNullOrWhiteSpace(pyScript)) return;
			this.pyEngine = pyEngine;
			pySource = this.pyEngine.CreateScriptSourceFromString(pyScript);
			pyScope = this.pyEngine.CreateScope();
			pySource.Execute(pyScope);
			pyClass = pyScope.GetVariable(scriptClassName);
			PyInstance = this.pyEngine.Operations.CreateInstance(pyClass);
			PyInstance.setup(); //Setup the script
			HasSharedResources = PyInstance.hasSharedResources;
			if (HasSharedResources) {
				SharedResourcesInputKeys = ((PythonList)PyInstance.sharedResourcesInputKeys)
					.ToList().Select(x => (string)x).ToList();
				SharedResourcesOutputKeys = ((PythonList)PyInstance.sharedResourcesOutputKeys)
					.ToList().Select(x => (string)x).ToList();
				FileResourceNames = ((PythonList)PyInstance.fileResourceNames)
					.ToList().Select(x => (string)x).ToList();
			}
			IsReady = PyInstance.isReady;
		}

		public void Execute(string text, Dictionary<string, object> pyDiagSharedResources = null, Dictionary<string, object> pyDiagFileResources = null) {
			//Check if the script is ready
			if (IsReady) {
				//Check if the script has shared resources and the resources needed are listed
				if (HasSharedResources && pyDiagSharedResources != null &&
					((SharedResourcesOutputKeys != null && SharedResourcesOutputKeys.Count > 0) ||
					(FileResourceNames != null && FileResourceNames.Count > 0))
					//Also check if all the file resources are available
					) {
					//We first check if we do not already have all the needed shared resources (both file and output)
					bool areAllFileResourcesAvailable =
						FileResourceNames == null || FileResourceNames.Count <= 0 ||
						FileResourceNames.All(x => pyDiagSharedResources.ContainsKey(
							Path.Combine(SipebiPythonManager.DIAG_DIR_NAME, SipebiPythonManager.DATA_DIR_NAME, x)));
					bool areAllOutputResourcesAvailable =
						SharedResourcesOutputKeys == null || SharedResourcesOutputKeys.Count <= 0 ||
						SharedResourcesOutputKeys.All(x => pyDiagSharedResources.ContainsKey(x));
					bool areAllSharedResourcesAvailable = areAllOutputResourcesAvailable && areAllFileResourcesAvailable;

					//If not all the file resources are available
					//Get the file resources from the Python Manager
					if (!areAllFileResourcesAvailable && pyDiagFileResources != null) {
						foreach (var fileName in FileResourceNames) {
							string fileResourceName = Path.Combine(SipebiPythonManager.DIAG_DIR_NAME, SipebiPythonManager.DATA_DIR_NAME, fileName);
							if (pyDiagFileResources.ContainsKey(fileResourceName))
								pyDiagSharedResources.Add(fileResourceName, pyDiagFileResources[fileResourceName]);
						}
						areAllFileResourcesAvailable = FileResourceNames.All(x => pyDiagSharedResources.ContainsKey(
							Path.Combine(SipebiPythonManager.DIAG_DIR_NAME, SipebiPythonManager.DATA_DIR_NAME, x)));
					}
					

					//If not all the output shared resources are available
					//We check if we could provide the inputs needed to create the output shared resources
					if (!areAllOutputResourcesAvailable &&
						(SharedResourcesInputKeys == null || SharedResourcesInputKeys.Count <= 0 || //Either there is no input key needed
						SharedResourcesInputKeys.All(x => pyDiagSharedResources.ContainsKey(x)) //Or all inputs are available
						)) {
						//Prepare all the input resources needed to create the shared resources
						PythonDictionary pyInputDict = new PythonDictionary();
						foreach (var sr in SharedResourcesInputKeys)
							pyInputDict.Add(sr, pyDiagSharedResources[sr]);
						PythonDictionary pyOutputDict = PyInstance.create_shared_resources(text, pyInputDict);

						//If the output resources are created successfully
						if (pyOutputDict != null && pyOutputDict.Count > 0)
							//We first update our shared resources with this new output wherever applicable
							foreach (var sr in pyOutputDict) {
								//WARNING! the outputs may replace the existing shared resources
								if (pyDiagSharedResources.ContainsKey((string)sr.Key))
									pyDiagSharedResources[(string)sr.Key] = sr.Value;
								else
									pyDiagSharedResources.Add(((string)sr.Key), sr.Value);
							}

						//We check again if we truly have all the output shared resources needed again here
						areAllOutputResourcesAvailable = 
							SharedResourcesOutputKeys.All(x => pyDiagSharedResources.ContainsKey(x));
					}

					//We check again if all the needed resources are available here (both file and output)
					areAllSharedResourcesAvailable = areAllOutputResourcesAvailable && areAllFileResourcesAvailable;

					//If we have all the shared resources needed, we will execute the script with the shared resources needed as the inputs + original text
					if (areAllSharedResourcesAvailable) {
						PythonDictionary pySharedDict = new PythonDictionary();
						foreach (var sr in SharedResourcesOutputKeys)
							pySharedDict.Add(sr, pyDiagSharedResources[sr]);
						PyInstance.pre_execute();
						PyInstance.execute_with_shared_resources(text, pySharedDict);
						PyInstance.post_execute();
					}
					//else if we:
					//(1a) do not have all the needed shared resources (both output and file) and
					//(1b) are unable to create them because the inputs are not fully available
					//or 
					//(2) unable to create all the needed shared resources until the end for some reason
					//we have no choice but to run a standard execution
					//  as long as it is possible
					else if (PyInstance.require_shared_resources() == false) {
						PyInstance.pre_execute();
						PyInstance.execute(text);
						PyInstance.post_execute();
					} else {
						StringBuilder sb = new StringBuilder();
						if (FileResourceNames != null && FileResourceNames.Count > 0)
							sb.AppendLine("File Resources: " + string.Join(", ", FileResourceNames));
						if (SharedResourcesOutputKeys != null && SharedResourcesOutputKeys.Count > 0)
							sb.AppendLine("Output Resources: " + string.Join(", ", SharedResourcesOutputKeys));
						throw new Exception($"Script [{pyScriptPath}] does not have all the necessary resources:" +
							Environment.NewLine + sb.ToString() + "to be executed!");
					}
				}
				//Script without shared resources shall be executed with only the original text as the argument
				//  as long as it is possible
				else if (PyInstance.require_shared_resources() == false) {
					PyInstance.pre_execute();
					PyInstance.execute(text);
					PyInstance.post_execute();
				} else {
					StringBuilder sb = new StringBuilder();
					if (FileResourceNames != null && FileResourceNames.Count > 0)
						sb.AppendLine("File Resources: " + string.Join(", ", FileResourceNames));
					if (SharedResourcesOutputKeys != null && SharedResourcesOutputKeys.Count > 0)
						sb.AppendLine("Output Resources: " + string.Join(", ", SharedResourcesOutputKeys));
					throw new Exception($"Script [{pyScriptPath}] does not have all the necessary resources:" +
						Environment.NewLine + sb.ToString());
				}
			} else //We will throw exception if the script is not ready by now
				throw new Exception($"Script [{pyScriptPath}] is not ready!");
		}

		//Method to reformat original Python script into script that is executable using this SipebiPythonScript
		private static string reformatScript(string originalPyScript) {
			if (string.IsNullOrWhiteSpace(originalPyScript)) return originalPyScript;
			string[] lines = originalPyScript.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
			StringBuilder sb = new StringBuilder();
			foreach (string line in lines) {
				if (!line.Trim().StartsWith("from ")) {
					sb.AppendLine(line);
					continue;
				}
				//i.e. from diag.PyDiagExampleClass import PyDiagExampleClass
				string[] lineParts = line.Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries);
				List<string> reformattedParts = new List<string>();
				foreach (string linePart in lineParts) {
					if (!linePart.Contains('.')) {
						reformattedParts.Add(linePart);
						continue;
					}
					//i.e. diag.PyDiagExampleClass
					string[] subParts = linePart.Split(new string[] { "." }, StringSplitOptions.RemoveEmptyEntries);
					reformattedParts.Add(subParts[subParts.Length - 1]); //Just add the last subpart (i.e. PyDiagExampleClass)
				}
				sb.AppendLine(string.Join(" ", reformattedParts));
			}
			return sb.ToString();
		}

		//Method to get (re-)formatted script, ready to be used by this SipebiPythonScript, from a file path
		private static string getFormattedScript(string scriptPath) {
			string script = File.ReadAllText(scriptPath);
			script = reformatScript(script);
			return script;
		}
	}
}
