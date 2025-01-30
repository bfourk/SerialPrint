using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace SerialPrinter;

public static class sPrinter
{
	private const string ConsoleName = "Serial Printer"; // Name shown in the console
	private const int TempInterval = 1; // How long (in seconds) the printer's asked for temperature
	private const int ScreenRefresh = 1; // How often (in seconds) the console should refresh
	// ^ Note: the screen updates after every instruction alongside this
	private const string TempRegex = @"\d*\.\d*";

	private static string ExtruderTemp = "0";
	private static string ExtruderTempMax = "0";
	private static string BedTemp = "0";
	private static string BedTempMax = "0";

	private static Stopwatch TotalTime = new Stopwatch();
	private static Stopwatch IntervalTime = new Stopwatch();
	private static Stopwatch ScreenTime = new Stopwatch();

	private static string CurrentInstruction = "";
	private static string PrinterStatus = "default";

	private static SerialPort? serialCon = null;

	// Removes comments from a given GCode instruction
	private static string StripGCode(string instruction)
	{
		string ret = "";
		for (int i = 0; i < instruction.Length; i++)
		{
			string instChar = instruction.Substring(i, 1);
			if (instChar == ";")
				break;
			ret += instChar;
		}
		return ret;
	}

	// Get the pty/tty port that the printer is at. Asks user when multiple are detected
	private static string? GetPrinterPort()
	{
		List<string> loopedDevices = new List<string>();
		string[] devices = Directory.GetFiles("/dev");
		foreach (string dev in devices)
			if (dev.StartsWith("/dev/ttyUSB") || dev.StartsWith("/dev/ttyACM"))
				loopedDevices.Add(dev);

		string[] foundDevices = loopedDevices.ToArray();

		if (foundDevices.Length == 0)
			return null;
		if (foundDevices.Length == 1)
			return foundDevices[0];

		int index = 0;
		Console.WriteLine("Multiple serial devices found:");
		foreach (string dev in foundDevices)
		{
			Console.WriteLine("{0}: {1}", index + 1, dev);
			index++;
		}
		while (true)
		{
			int choice = -1;
			try
			{
				choice = Convert.ToInt32(Console.ReadLine());
			}
			catch
			{
				Console.WriteLine("Please input a number");
				continue;
			}
			if (choice < 1)
			{
				Console.WriteLine("Number must be positive");
				continue;
			}
			if (choice > foundDevices.Length)
			{
				Console.WriteLine("Number must be between 1 and {0}", foundDevices.Length);
				continue;
			}
			return foundDevices[choice - 1];
		}
	}

	// Writes print-info to the screen
	private static void WriteScreen()
	{
		int ConsoleWidth = Console.WindowWidth;
		float PerBar = (ConsoleWidth - ConsoleName.Length) / 2; // 13 is name length

		Console.Clear();

		Console.Write(new string('=', (int)PerBar));
		Console.Write(ConsoleName);
		Console.WriteLine(new string('=', ConsoleWidth % 2 == 0 ? (int)PerBar : (int)PerBar+1));
		Console.WriteLine("Status: {0}", PrinterStatus);
		Console.WriteLine("Ext. Temp: {0}/{1}°C", ExtruderTemp, ExtruderTempMax);
		Console.WriteLine("Bed Temp: {0}/{1}°C", BedTemp, BedTempMax);

		for (int i = 0; i < Console.WindowHeight - 7; i++)
			Console.WriteLine();

		TimeSpan ts = TotalTime.Elapsed;
		Console.Write("Elapsed: ");
		if (ts.Hours > 0)
			Console.Write("{0} Hour{1}, ", ts.Hours, ts.Hours == 1 ? "" : "s");
		if (ts.Minutes > 0)
			Console.Write("{0} Minute{1}, ", ts.Minutes, ts.Minutes == 1 ? "" : "s");
		Console.WriteLine("{0} Second{1}", ts.Seconds, ts.Seconds == 1 ? "" : "s");
		Console.WriteLine("Inst: {0}", CurrentInstruction);
		Console.Write(new string('=', ConsoleWidth));
	}

	/*
		Interpretes responses from the printer. Updates temperatures/status when necessary.
		false = busy/not ready
		true = ready for next instruction
	*/
	private static bool ParseResponse(string resp)
	{
		resp = resp.Trim();

		// Check for temps first
		if (resp.StartsWith("T:") || resp.StartsWith("ok T:")) // Temperature report
		{
			MatchCollection matches = Regex.Matches(resp, TempRegex);
			if (matches.Count >= 4)
			{
				ExtruderTemp = matches[0].ToString();
				ExtruderTempMax = matches[1].ToString();
				BedTemp = matches[2].ToString();
				BedTempMax = matches[3].ToString();
			}
		}
		if (resp.StartsWith("ok T:")) // Ensure we do not interpret the temperature's OK message as a move/temp OK message
			return false;

		if (resp.StartsWith("ok"))
		{
			PrinterStatus = "Printing";
			return true;
		}
		if (resp == "echo:busy: processing")
			PrinterStatus = "Heating";
		return false;
	}

	// TODO: Fix ctrl-c implementation
	// Currently just exits if used during printing instead of turning off extruder/bed/fan
	public static void Main(string[] args)
	{
		Console.CancelKeyPress += delegate {
			Console.WriteLine("\nCtrl-C detected");
			// serialCon only exists when printing
			if (serialCon == null) // not printing, just exit
				return;
			Console.WriteLine("Stopping printer");
			serialCon.WriteLine("M104 S0");
			serialCon.WriteLine("M140 S0");
			serialCon.WriteLine("M106 S0");
			serialCon.Close();
			Console.WriteLine("Exiting");
			Environment.Exit(0);
		};
		// Platform checks
		if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Console.WriteLine("This program does not work on Windows");
			Environment.Exit(1);
		}
		if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			Console.WriteLine("This program has not been tested on OSX, it may not work");

		// Get the serial port the printer is on
		string? port = GetPrinterPort();
		if (port == null)
		{
			Console.WriteLine("Printer port not found, is your printer connected?\nWaiting for printer...");
			while (true)
			{
				port = GetPrinterPort();
				if (port != null)
					break;
				Thread.Sleep(1000);
			}
		}
		
		Console.WriteLine("Found printer at port {0}", port);

		serialCon = new SerialPort(port);
		serialCon.BaudRate = 115200;

		// Get path to GCode file
		string filePath = "";
		while (true)
		{
			Console.Write("Input GCode file path: ");
			string? path = Console.ReadLine();
			if (path == null || !File.Exists(path))
			{
				Console.WriteLine("\nFile not found");
				continue;
			}
			filePath = path;
			break;
		}

		// Attempt to communicate with the printer
		Console.WriteLine("Connecting to printer");
		serialCon.Open();
		Console.WriteLine("Connected, testing connection");
		serialCon.WriteLine("M115"); // Tell the printer to send it's firmware info
		string resp = serialCon.ReadLine();
		Console.WriteLine("Printer responds: {0}", resp);

		string[]? gcode = new string[0];
		try
		{
			gcode = File.ReadAllLines(filePath);
		}
		catch (Exception ex)
		{
			Console.WriteLine("Failed to open GCode file: {0}", ex.StackTrace);
			Environment.Exit(1);
		}
		Console.WriteLine("Loaded GCode, running");
		IntervalTime.Start();
		TotalTime.Start();
		ScreenTime.Start();
		for (int i = 0; i < gcode.Length; i++)
		{
			if (IntervalTime.Elapsed.Seconds >= TempInterval)
			{
				IntervalTime.Restart();
				serialCon.WriteLine("M105");
			}
			string instruction = gcode[i].Trim();
			if (instruction == "") // Empty line, ignore
				continue;
			if (instruction.StartsWith(";")) // Line is a comment, ignore
				continue;
			if (instruction.StartsWith("M105")) // Temperature request, ignore
				continue;

			instruction = StripGCode(instruction); // Strip comments from the GCode instruction
			CurrentInstruction = instruction;
			serialCon.WriteLine(instruction);
			WriteScreen();

			// Parse response from printer
			while (true)
			{
				if (ScreenTime.Elapsed.Seconds >= ScreenRefresh)
				{
					ScreenTime.Restart();
					WriteScreen();
				}
				if (ParseResponse(serialCon.ReadLine()))
					break;
				Thread.Sleep(1);
			}
		}
		Console.Clear();
		TimeSpan ts = TotalTime.Elapsed;
		Console.Write("Print finished in ");
		// Copied from WriteScreen function
		if (ts.Hours > 0)
			Console.Write("{0} Hour{1}, ", ts.Hours, ts.Hours == 1 ? "" : "s");
		if (ts.Minutes > 0)
			Console.Write("{0} Minute{1}, ", ts.Minutes, ts.Minutes == 1 ? "" : "s");
		Console.WriteLine("{0} Second{1}", ts.Seconds, ts.Seconds == 1 ? "" : "s");
	}
}