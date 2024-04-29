using System.Collections.Generic;
using Vintagestory.API.Common;

namespace grieflogger
{
	public class ConfigSettings
	{

		public int BROKEN_LINE_LIMIT { get; set; } = 10000;

		public string GRIEF_KEY { get; set; } = "GRIEF_KEY";

		public string[] CHAT_CODE_WHITELIST { get; set; } = new string[]
            { "game:soil-medium-none", /* rest of elements */ };

		public string CHAT_GROUP { get; set; } = "InfoLogChatGroup";

        public string[] CHAT_ROLES = new string[]
        { "suvisitor", "crvisitor", "limitedsuplayer", "limitedcrplayer", "suplayer", "crplayer", "sumod", "crmod", "admin" };
    }
}