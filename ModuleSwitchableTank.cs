﻿//   ModuleSwitchableTank.cs
//
//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2016 Allis Tauri

using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace AT_Utils
{
	/// <summary>
	/// This is a different approach than in ModularFuelTanks, more suitable for "cargo" resources than fuels:
	/// Such tank may contain only one type of resources, but this type may be switched in-flight, 
	/// if the part has zero amount of the current resource.
	/// </summary>
	public class ModuleSwitchableTank : AbstractResourceTank
	{
		const string   RES_MANAGED = "Res";
		const string RES_UNMANAGED = "N/A";

		bool enable_part_controls = true;
		public bool EnablePartControls 
		{ 
			get { return enable_part_controls; } 
			set 
			{ 
				enable_part_controls = value;
				disable_part_controls();
				init_type_control(); 
				init_res_control();
			}
		}

		[KSPField(isPersistant = true)] public int id = -1;
		[KSPField(isPersistant = true)] public bool managed;

		/// <summary>
		/// If a tank type can be selected in editor.
		/// </summary>
		[KSPField] public bool ChooseTankType;

		/// <summary>
		/// Excluded tank types. If empty, all types are supported.
		/// </summary>
		[KSPField] public string ExcludeTankTypes = string.Empty;
		string[] exclude;

		/// <summary>
		/// Supported tank types. If empty, all types are supported. Overrides ExcludedTankTypes.
		/// </summary>
		[KSPField] public string IncludeTankTypes = string.Empty;
		string[] include;

		/// <summary>
		/// The type of the tank. Types are defined in separate config nodes. Cannot be changed in flight.
		/// </summary>
		[KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Type")]
		[UI_ChooseOption(scene = UI_Scene.Editor)]
		public string TankType;
		SwitchableTankType tank_type;
		public SwitchableTankType Type { get { return tank_type; } }
		public List<string> SupportedTypes = new List<string>();

		/// <summary>
		/// Cost of an empty tank of current type and volume
		/// </summary>
		public float Cost { get { return tank_type != null? tank_type.Cost(Volume) : 0; } }

		/// <summary>
		/// Additional mass of an empty tank of current type and volume
		/// </summary>
		public float AddMass { get { return tank_type != null? tank_type.AddMass(Volume) : 0; } }

		/// <summary>
		/// The initial partial amount of the CurrentResource.
		/// Should be in the [0, 1] interval.
		/// </summary>
		[KSPField(isPersistant = true)] public float InitialAmount;

		/// <summary>
		/// The name of a currently selected resource. Can be changed in flight if resource amount is zero.
		/// </summary>
		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = RES_MANAGED)]
		[UI_ChooseOption]
		public string CurrentResource = string.Empty;
		TankResource resource_info;
		PartResource current_resource;
		string previous_resource = string.Empty;

		#if DEBUG
		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = false, guiName = "Part", guiUnits = "°C")]
		public string PartTemperatureDisplay = "0";
		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = false, guiName = "Skin", guiUnits = "°C")]
		public string SkinTemperatureDisplay = "0";
		#endif

		[KSPField(isPersistant = true, guiActive = false, guiActiveEditor = false, guiName = "T(Res)", guiUnits = "°C")]
		public string TemperatureDisplay = "0";
		ResourceBoiloff boiloff;

		[KSPField(isPersistant = true, guiActive = false, guiActiveEditor = false, guiName = "Cooling", guiFormat = "P1")]
		public double CoolingDisplay = 0;
		ActiveCooling cooler;

		public float Usage { get { return current_resource != null? (float)(current_resource.amount/current_resource.maxAmount) : 0; } }
		public string ResourceInUse { get { return current_resource != null? current_resource.resourceName : string.Empty; } }
		public PartResource Resource { get { return current_resource; } }
		public double Amount 
		{
			get { return current_resource != null? current_resource.amount : 0; }
			set { if(current_resource != null) current_resource.amount = Utils.Clamp(value, 0, current_resource.maxAmount); }
		}
		public double MaxAmount 
		{
			get { return current_resource != null? current_resource.maxAmount : 0; }
			set { if(current_resource != null) current_resource.maxAmount = value; }
		}
		public float MaxResourceInVolume 
		{ 
			get 
			{ 
				return tank_type == null || resource_info == null? 
					0 : Volume * tank_type.UsefulVolumeRatio * resource_info.UnitsPerLiter*1000f; 
			} 
		}

		readonly List<ModuleSwitchableTank> other_tanks = new List<ModuleSwitchableTank>();

		public override string GetInfo()
		{
			var info = "";
			init_supported_types();
			if(ChooseTankType) 
				info += SwitchableTankType.TypesInfo(include, exclude);
			if(!init_tank_type()) return info;
			info += tank_type.Info;
			info += "Tank Volume: " + Utils.formatVolume(Volume);
			return info;
		}

		protected override float TankMass(float defaultMass)
		{
			if(tank_type == null && !init_tank_type()) return 0;
			return AddMass;
		}

		protected override float TankCost(float defaultCost)
		{ 
			if(tank_type == null && !init_tank_type()) return 0;
			return Cost;
		}

		protected override float ResourcesCost(bool maxAmount = true)
		{
			var cost = 0f;
			if(current_resource != null)
				cost = (float)current_resource.maxAmount*current_resource.info.unitCost;
			else
			{
				if(tank_type == null && !init_tank_type()) return 0;
				resource_info = tank_type[CurrentResource];
				if(resource_info == null) return 0;
				cost = MaxResourceInVolume * resource_info.Resource.unitCost;
			}
			return maxAmount? cost : cost * InitialAmount;
		}

		protected override float ResourcesMass(bool maxAmount = true)
		{
			var mass = 0f;
			if(current_resource != null)
				mass = (float)current_resource.maxAmount*current_resource.info.density;
			else
			{
				if(tank_type == null && !init_tank_type()) return 0;
				resource_info = tank_type[CurrentResource];
				if(resource_info == null) return 0;
				mass = MaxResourceInVolume * resource_info.Resource.density;
			}
			return maxAmount? mass : mass * InitialAmount;
		}

		void OnDestroy() { Utils.UpdateEditorGUI(); }

		void init_supported_types()
		{
			include = Utils.ParseLine(IncludeTankTypes, Utils.Comma);
			exclude = Utils.ParseLine(ExcludeTankTypes, Utils.Comma);
			SupportedTypes = SwitchableTankType.TankTypeNames(include, exclude);
		}

		public override void OnStart(StartState state)
		{
			init_supported_types();
			//get other tanks in this part
			other_tanks.AddRange(from t in part.Modules.GetModules<ModuleSwitchableTank>()
								 where t != this select t);
			//initialize tank type chooser
			disable_part_controls();
			if(state == StartState.Editor) 
				init_type_control();
			init_tank_type();
			init_resource();
			init_res_control();
			StartCoroutine(slow_update());
		}

		public override void OnLoad(ConfigNode node)
		{
			//if the tank is managed, save its config
			if(node.HasValue(SwitchableTankManager.MANAGED)) 
			{
				ModuleSave = node;
				managed = true;
			}
			//if the node is not from a TankManager, but we have a saved config, reload it
			else if(ModuleSave != null && 
			        ModuleSave.HasValue(SwitchableTankManager.MANAGED))	
				Load(ModuleSave);
			//if we still don't have a saved module config, save it
			else if(ModuleSave == null)
				ModuleSave = node;
		}

		public override void OnSave(ConfigNode node)
		{
			if(current_resource != null)
				InitialAmount = (float)(current_resource.amount/current_resource.maxAmount);
			base.OnSave(node);
			if(boiloff != null) 
				boiloff.SaveInto(node);
		}

		//workaround for ConfigNode non-serialization
		public byte[] _module_save;
		public void OnBeforeSerialize()
		{ _module_save = ConfigNodeWrapper.SaveConfigNode(ModuleSave); }
		public void OnAfterDeserialize() 
		{ ModuleSave = ConfigNodeWrapper.RestoreConfigNode(_module_save); }

		/// <summary>
		/// Adds the given SwitchableTank to the list of all tanks 
		/// whose CurrentResource is checked upon resource switching.
		/// </summary>
		public void RegisterOtherTank(ModuleSwitchableTank tank)
		{ if(!other_tanks.Contains(tank)) other_tanks.Add(tank); }

		/// <summary>
		/// Remoes the given SwitchableTank from the list of all tanks 
		/// whose CurrentResource is checked upon resource switching.
		/// </summary>
		public bool UnregisterOtherTank(ModuleSwitchableTank tank)
		{ return other_tanks.Remove(tank); }

		/// <summary>
		/// If some resource is currently managed by the tank, checks 
		/// if its amount is zero and, if so, removes the resource from the part.
		/// </summary>
		/// <returns><c>true</c>, if resource was removed or was not present, 
		/// <c>false</c> otherwise.</returns>
		public bool TryRemoveResource()
		{
			if(current_resource == null) return true;
			if(HighLogic.LoadedSceneIsEditor) 
				current_resource.amount = 0;
		   	if(current_resource.amount > 0)
			{ 
				Utils.Message("Tank is in use");
				CurrentResource = current_resource.resourceName;
				if(tank_type != null) TankType = tank_type.name;
				return false;
			}
			part.Resources.Remove(current_resource.resourceName);
			current_resource = null;
			return true;
		}

		void init_type_control()
		{
			if(!enable_part_controls || !ChooseTankType || SupportedTypes.Count <= 1) return;
			var tank_names = SupportedTypes.Select(Utils.ParseCamelCase).ToArray();
			Utils.SetupChooser(tank_names, SupportedTypes.ToArray(), Fields["TankType"]);
			Utils.EnableField(Fields["TankType"]);
		}

		void update_cooler_control()
		{
			if(cooler == null) return;
			Events["ToggleCooler"].guiName = cooler.Enabled? "Disable Cooling" : "Enable Cooling";
		}

		void update_boiloff_control()
		{
			if(boiloff != null && boiloff.Valid) 
			{
				var res_name = Utils.ParseCamelCase(current_resource.resourceName);
				Fields["TemperatureDisplay"].guiActive = true;
				Fields["TemperatureDisplay"].guiName = res_name;
				if(cooler != null)
				{
					Fields["CoolingDisplay"].guiActive = true;
					Fields["CoolingDisplay"].guiName = res_name+" Cooling";
					Events["ToggleCooler"].active = true;
					update_cooler_control();
				}
				else 
				{
					Fields["CoolingDisplay"].guiActive = false;
					Events["ToggleCooler"].active = false;
				}
			}
			else
			{
				Fields["TemperatureDisplay"].guiActive = false;
				Fields["CoolingDisplay"].guiActive = false;
				Events["ToggleCooler"].active = false;
			}
		}

		void init_res_control()
		{
			if(tank_type == null || !enable_part_controls || tank_type.Resources.Count <= 1) 
				Utils.EnableField(Fields["CurrentResource"], false);
			else
			{
				var res_values = tank_type.ResourceNames.ToArray();
				var res_names  = tank_type.ResourceNames.Select(Utils.ParseCamelCase).ToArray();
				Utils.SetupChooser(res_names, res_values, Fields["CurrentResource"]);
				Utils.EnableField(Fields["CurrentResource"]);
			}
			update_boiloff_control();
			part.UpdatePartMenu();
		}

		void update_res_control()
		{
			Fields["CurrentResource"].guiName = current_resource == null ? RES_UNMANAGED : RES_MANAGED;
			update_boiloff_control();
			part.UpdatePartMenu();
		}

		void disable_part_controls()
		{
			Utils.EnableField(Fields["TankType"], false);
			Utils.EnableField(Fields["CurrentResource"], false);
		}

		bool init_tank_type()
		{
			if(Volume < 0) Volume = Metric.Volume(part);
			if(tank_type != null) return true;
			boiloff = null;
			//if tank type is not provided, use the first one from the library
			if(string.IsNullOrEmpty(TankType))
			{ TankType = SwitchableTankType.TankTypeNames(include, exclude)[0]; }
			//select tank type from the library
			if(!SwitchableTankType.TankTypes.TryGetValue(TankType, out tank_type))
				Utils.Message(6, "No \"{0}\" tank type in the library.\n" +
				              "Configuration of \"{1}\" is INVALID.", 
				              TankType, this.Title());
			if(tank_type == null) return false;
			//initialize current resource
			if(CurrentResource == string.Empty || 
			   !tank_type.Resources.ContainsKey(CurrentResource)) 
				CurrentResource = tank_type.DefaultResource.Name;
			//initialize boiloff/cooling
			if(tank_type.Boiloff || tank_type.Cooling)
			{
				boiloff = tank_type.Boiloff? new ResourceBoiloff() : new ActiveCooling();
				if(ModuleSave != null) boiloff.LoadFrom(ModuleSave);
				cooler = boiloff as ActiveCooling;
			}
			return true;
		}

		void change_tank_type()
		{
			//check if the tank is in use
			if(tank_type != null && 
			   current_resource != null &&
			   current_resource.amount > 0)
			{ 
				if(HighLogic.LoadedSceneIsEditor) 
					current_resource.amount = 0;
				else
				{
					Utils.Message("Cannot change tank type while tank is in use");
					TankType = tank_type.name;
					return;
				}
			}
			//setup new tank type
			tank_type = null;
			init_tank_type();
			switch_resource();
			init_res_control();
		}

		/// <summary>
		/// Check if the resource 'res' is managed by any other tank.
		/// </summary>
		/// <returns><c>true</c>, if resource is used, <c>false</c> otherwise.</returns>
		/// <param name="res">resource name</param>
		bool resource_in_use(string res)
		{ return other_tanks.Any(t => t.ResourceInUse == res); }

		public void UpdateMaxAmount(bool update_amount = false)
		{
			if(current_resource == null) return;
			var max_amount = current_resource.maxAmount;
			current_resource.maxAmount = MaxResourceInVolume;
			if(current_resource.amount > current_resource.maxAmount)
				current_resource.amount = current_resource.maxAmount;
			else if(update_amount && max_amount > 0) 
				current_resource.amount *= current_resource.maxAmount/max_amount;
			part.UpdatePartMenu();
		}

		public void SetVolume(float volume, bool update_amount = false)
		{
			Volume = volume;
			UpdateMaxAmount(update_amount);
		}

		bool init_resource()
		{
			if(current_resource != null)
			{
				CurrentResource = current_resource.resourceName;
				return false;
			}
			if(tank_type == null) return false;
			//check if this is tank initialization or switching
			var initializing = previous_resource == string.Empty;
			previous_resource = CurrentResource;
			//check if the resource is in use by another tank
			if(resource_in_use(CurrentResource)) 
			{
				Utils.Message(6, "A part cannot have more than one resource of any type");
				return false;
			}
			//get definition of the next not-managed resource
			resource_info = tank_type[CurrentResource];
			var maxAmount = MaxResourceInVolume;
			//if there is such resource already, just plug it in
			var part_res = part.Resources[resource_info.Name];
			if(part_res != null) 
			{ 
				current_resource = part_res;
				//do not change resource amount/maxAmount in flight, unless we have none
				if(HighLogic.LoadedSceneIsEditor || current_resource.amount.Equals(0))
				{
					current_resource.maxAmount = maxAmount;
					if(current_resource.amount > current_resource.maxAmount)
						current_resource.amount = current_resource.maxAmount;
				}
			}
			else //create the new resource
			{
				var node = new ConfigNode("RESOURCE");
				node.AddValue("name", resource_info.Name);
				node.AddValue("amount", initializing? maxAmount*InitialAmount : 0);
				node.AddValue("maxAmount", maxAmount);
				current_resource = part.Resources.Add(node);
			}
			if(boiloff != null) boiloff.SetResource(current_resource);
			if(part.Events != null) part.SendEvent("resource_changed");
			return true;
		}

		bool switch_resource()
		{ return TryRemoveResource() && init_resource(); }

		[KSPEvent]
		void resource_changed()
		{
			if(current_resource != null) return;
			switch_resource();
		}

		//interface for ProceduralParts
		[KSPEvent(guiActive=false, active = true)]
		void OnPartVolumeChanged(BaseEventData data)
		{
			if(managed) return;
			var volName = data.Get<string>("volName");
			var newTotalVolume = (float)data.Get<double>("newTotalVolume");
			if(volName == "Tankage") SetVolume(newTotalVolume, HighLogic.LoadedSceneIsEditor);
		}

		//interface for TweakScale
		[KSPEvent(guiActive=false, active = true)]
		void OnPartScaleChanged(BaseEventData data)
		{
			if(managed) return;
			var scale = data.Get<float>("factorRelative");
			SetVolume(Volume*scale*scale*scale);
		}

		[KSPEvent(guiActive=true, guiName = "Disable Cooling", active = false)]
		void ToggleCooler()
		{
			if(cooler == null) return;
			cooler.Enabled = !cooler.Enabled;
			update_cooler_control();
		}

		IEnumerator<YieldInstruction> slow_update()
		{
			while(true)
			{
				if(HighLogic.LoadedSceneIsEditor)
				{
					if(tank_type == null || tank_type.name != TankType) 
						change_tank_type();
				}
				else if(tank_type != null && tank_type.name != TankType)
				{
					Utils.Message("Cannot change the type of the already constructed tank");
					TankType = tank_type.name;
				}
				if(CurrentResource != previous_resource)
				{ switch_resource(); update_res_control(); }

				if(HighLogic.LoadedSceneIsFlight)
				{
					//temprerature display
					if(boiloff != null)
						TemperatureDisplay = (boiloff.CoreTemperature-273.15).ToString("F1");
					if(cooler != null)
					{
						CoolingDisplay = cooler.IsCooling? cooler.CoolingEfficiency : 0;
						update_cooler_control();
					}
					#if DEBUG
					PartTemperatureDisplay = (part.temperature-273.15).ToString("F1");
					SkinTemperatureDisplay = (part.skinTemperature-273.15).ToString("F1");
					#endif
				}
				yield return new WaitForSeconds(0.1f);
			}
		}

		void FixedUpdate()
		{
			if(boiloff != null) 
				boiloff.FixedUpdate();
		}
	}

	public class SwitchableTankUpdater : ModuleUpdater<ModuleSwitchableTank>
	{
		protected override void on_rescale(ModulePair<ModuleSwitchableTank> mp, Scale scale)
		{ 
			mp.module.SetVolume(mp.module.Volume*scale.relative.volume);
		}
	}
}

