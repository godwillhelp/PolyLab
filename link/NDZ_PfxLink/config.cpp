class CfgPatches
{
	class NDZ_PfxLink
	{
		units[] = {};
		weapons[] = {};
		requiredVersion = 0.1;
		requiredAddons[] =
		{
			"DZ_Scripts"
		};
	};
};

class CfgMods
{
	class NDZ_PfxLink
	{
		dir = "NDZ_PfxLink";
		name = "NDZ Particle Studio link";
		type = "mod";
		dependencies[] = {"Mission"};
		class defs
		{
			class missionScriptModule
			{
				value = "";
				files[] = {"NDZ_PfxLink/Scripts/5_Mission"};
			};
		};
	};
};
