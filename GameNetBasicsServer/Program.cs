using System;

namespace GameNetBasicsServer
{
	public static class Program
	{
		[STAThread]
		static void Main()
		{
			using (var game = new ServerGame())
				game.Run();
		}
	}
}
