namespace Uplift.Conversion;

/// <summary>
///     The names GS2 engines define globally (the client's and the server's script help, section Global): a bare one is the engine's,
///     never a GS1 flag of the player's.
/// </summary>
internal static class BuiltInNames
{
	public static readonly HashSet<string> Globals = new(StringComparer.OrdinalIgnoreCase)
	{
		"addcontrol", "addmaterialmapping", "addtiledef", "addtiledef2", "aindexof", "allfeatures", "allplayerscount", "allrenderobjecttypes",
		"allstats", "arccos", "arcsin", "attachplayertoobj", "base64decode", "base64encode", "boxcontains", "boxcontainsvector", "boxintersect",
		"boxoverlaps", "boxscale", "callnpc", "callweapon", "canspin", "carriesblackstone", "carriesbush", "carriesnpc", "carriessign",
		"carriesstone", "carriesvase", "castray", "checksum", "contains", "copystrings", "cursoroff", "cursoron", "degtorad", "deletefile",
		"detachplayer", "disabledefmovement", "disablemap", "disablepause", "disableselectweapons", "disableweapons", "downloadfile", "downloadpos",
		"downloadsize", "echo", "editingmission", "emoticonchar", "enabledefmovement", "enablefeatures", "enablemap", "enablepause",
		"enableselectweapons", "enableweapons", "escapefilename", "escapestring", "explodebomb", "extractfilebase", "extractfileext",
		"extractfilename", "extractfilepath", "fileexists", "filesize", "fileupdate", "fileupdateignore", "findani", "findfiles", "findlevel",
		"findnearestplayer", "findnearestplayers", "findnpcbyid", "findpathinarray", "findplayer", "findplayerbycommunityname", "findplayerbyid",
		"findweapon", "focusx", "focusy", "format2", "freeallresources", "freefileresources", "freezeplayer", "get3dobjectat", "get3dobjectatmouse",
		"get3dobjectbyray", "get3dobjectsbybox", "get3dobjectsbyradius", "get3dscreenposition", "getanglesfromvector", "getascii", "getbasepackage",
		"getboxcenter", "getcallstack", "getdisplaydevicelist", "getdownloadedupdatepackagesize", "getdownloadingpackage",
		"getdownloadingpackagescount", "getdropz", "getextension", "getfilemodtime", "gethttprequest", "getimgheight", "getimgpixel", "getimgwidth",
		"getkeycode", "getloginaccountname", "getmapx", "getmapy", "getmusicfilename", "getmusicstatus", "getmusictags", "getnearestplayer",
		"getnearestplayers", "getpackagesdownloadcomplete", "getpackagesdownloaded", "getplatform", "getplayerlisticons", "getpremiumoption",
		"getresolutionlist", "getscriptaccessfile", "getservername", "getstringkeys", "gett9words", "getterraintexture", "getterraintextureindex",
		"gettextheight", "gettextwidth", "gettileset", "gettilesettype", "gettotalupdatepackagesize", "getupdatepackage", "getvectorfromangles",
		"getz", "ghostsnear", "graalcontrolhasfocus", "graalplugincookie", "graalversion", "gravity", "hideplayer", "hidesword", "hitnpc",
		"hitobjects", "hitplayer", "installedlanguages", "isadminguild", "isapplicationactive", "iscarrying", "isclassloaded", "iscursoron",
		"isdownloading", "isdownloadingfiles", "isfocused", "isfullscreenmode", "isgraal3d", "isgraalplugin", "isimgpixeltransparent",
		"isimgrectangletransparent", "isleader", "ismusicplaying", "isobject", "isofflinemode", "isonmap", "isopengl", "ispremiumversion",
		"isrecordingvideo", "issoundplaying", "isstreamingvideo", "jpegquality", "keydown", "keydown2", "keydown2global", "keydownglobal",
		"keyname", "keyname2", "lastdownloadfile", "lay2", "leftmousebutton", "leftmousebuttonglobal", "levelorgx", "levelorgy",
		"lighteffectsenabled", "lightscene", "loadclass", "loadmap", "loadt9words", "loadtranslation", "lowercase", "ltmfs", "ltmmax", "ltmmin",
		"makescreenshot2", "matrixcreate", "matrixcreatefromeuler", "matrixmulpoint", "matrixmultiply", "matrixmulvector", "md5",
		"middlemousebutton", "middlemousebuttonglobal", "mousebuttons", "mousebuttonsglobal", "mousepitch", "mousescreenx", "mousescreeny",
		"mousewheeldelta", "mousex", "mousey", "mouseyaw", "musiclen", "musicpos", "noplayerkilling", "onwall", "onwall2", "onwater", "onwater2",
		"opengraalurl", "openurl", "openurl2", "particleeffectsenabled", "play", "play2", "play3d", "playlooped", "playlooped2", "popdialog",
		"preloadanimation", "pushdialog", "putleaps", "radtodeg", "randomstring", "removeescapesfromfilename", "removetiledefs", "replaceani",
		"requestfiledeletion", "requestfilerename", "requestfilesmove", "requesthttp", "requesttext", "requesturl", "resetfocus",
		"rightmousebutton", "rightmousebuttonglobal", "rotationadd", "rotationaddeuler", "rotationfromeuler", "rotationsub", "rotationtoeuler",
		"rungarbagecollector", "savelog", "say", "say2", "screenheight", "screenwidth", "screenx", "screeny", "scriptedcontrols",
		"scriptedplayerlist", "scriptlogwritetoreadonly", "selectedlistplayers", "selectedsword", "selectedweapon", "selectfilefordownload",
		"selectfileforupload", "sendrpgmessage", "sendtext", "sendtorc", "servername", "serverstartconnect", "serverstartparams", "serverwarp",
		"setani", "setbeltcolor", "setcoatcolor", "setcontentcontrol", "setcursor2", "seteffect", "setfocus", "setfogcolors", "setgender",
		"sethead", "setinteriorrendermode", "setletters", "setminimap", "setmusicvolume", "setplayerdir", "setshield", "setshoecolor",
		"setshootparams", "setskincolor", "setskybandcolors", "setskybandsizes", "setsleevecolor", "setspritesimage", "setstatusimage",
		"setstreamingmode", "setsuncolors", "setsword", "setterrainrendermode", "shotbybaddy", "shotbyplayer", "showstats", "showterraingrid",
		"spritesimage", "spyfire", "startrecordvideo", "statusimage", "stopmidi", "stopmusic", "stoprecordvideo", "stopsound", "stopsounds",
		"strcmp", "strequals", "switchtodirectx", "switchtoopengl", "synctimeofday", "takeplayercarry", "takeplayerhorse", "testplayer", "timevar",
		"timevar2", "timevar3", "trace", "triggeraction", "triggerserver", "update3dterrain", "updateboard", "updateterrain",
		"updatevisibledistance", "uploadfile", "uppercase", "vectoradd", "vectorcross", "vectordist", "vectordot", "vectorlen", "vectornormalize",
		"vectororthobasis", "vectorscale", "vectorsub", "wasshooted", "waterheight", "weapons", "weaponsenabled", "weathereffectsenabled",
		"worldclockstopped", "worldhour", "worldminute", "worldminutesofday", "worldrealsecondsperday", "worldx", "worldy", "wraptext", "wraptext2"
	};
}
