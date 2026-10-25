class CfgPatches
{
	class PolyLab_PfxLink
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
	class PolyLab_PfxLink
	{
		dir = "PolyLab_PfxLink";
		name = "Poly Lab Particle Studio link";
		type = "mod";
		dependencies[] = {"Mission"};
		class defs
		{
			class missionScriptModule
			{
				value = "";
				files[] = {"PolyLab_PfxLink/Scripts/5_Mission"};
			};
		};
	};
};
