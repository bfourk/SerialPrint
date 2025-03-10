from serial import Serial
from time import time,sleep
from re import findall
import os

# Globals

ConsoleName = "Serial Printer"
TempInterval = 1 # How often the printer is asked for it's status
RefreshInterval = 1 # How often the terminal refreshes

TempRegex = r"\d*\.\d*"

# Variables

extruderTemp = "0"
extruderTempMax = "0"
bedTemp = "0"
bedTempMax = "0"

totalTime = 0
intervalClock = 0
screenClock = 0

currentInstruction = ""
printerStatus = "default"
serialPort = None

# Clears the console
def ClearScreen():
	if os.name == "nt":
		os.system("cls")
	else:
		os.system("clear")

# Removes comments from a given GCode instruction
def StripGCode(instruction):
	ret = ""
	for x in instruction:
		if x == ";" or x == None:
			break
		ret += x
	return ret

# Get the pty/tty port that the printer is at. Asks user when multiple are detected
def GetPrinterPort():
	loopedDevices = []
	for dev in os.listdir("/dev"):
		if dev.startswith("ttyUSB") or dev.startswith("ttyACM"):
			loopedDevices.append("/dev/{0}".format(dev))
	if len(loopedDevices) == 0:
		return None
	if len(loopedDevices) == 1:
		return loopedDevices[0];
	index = 0
	print("Multiple serial devices found")
	for dev in loopedDevices:
		print("{0}: {1}".format(index + 1, dev))
		index += 1
	while True:
		choice = -1
		try:
			choice = int(input())
		except:
			print("Please input a number")
			continue
		if choice < 1:
			print("Number must be positive")
			continue
		if choice > len(loopedDevices):
			print("Number must be between 1 and {0}".format(len(loopedDevices)))
			continue
		return loopedDevices[choice - 1]

# Converts seconds into it's hour, minute and second counterparts
def FormatSeconds(seconds):
	hours = int(seconds / 3600)
	seconds %= 3600
	minutes = int(seconds / 60)
	seconds %= 60
	seconds = int(seconds) # Fix formatting issues
	ret = ""
	if hours != 0:
		ret += "{0} Hour{1}".format(hours, ", " if (hours == 1) else "s, ")
	if minutes != 0:
		ret += "{0} Minute{1}".format(minutes, ", " if (minutes == 1) else "s, ")
	ret += "{0} Second{1}".format(seconds, "" if (seconds == 1) else "s")
	return ret

# Writes print-info to the screen
def WriteScreen():
	size = os.get_terminal_size()
	consoleWidth = size.columns
	perBar = int((consoleWidth - len(ConsoleName)) / 2)
	ClearScreen()

	print("="*perBar, end="")
	print(ConsoleName, end="")
	print("="*(perBar if consoleWidth % 2 == 0 else perBar+1))
	print("Status: {0}".format(printerStatus))
	print("Ext. Temp: {0}/{1}°C".format(extruderTemp, extruderTempMax))
	print("Bed Temp: {0}/{1}°C".format(bedTemp, bedTempMax))
	for x in range(0, size.lines - 7):
		print()
	print("Elapsed: {0}".format(FormatSeconds(time() - totalTime)))
	print("Inst: {0}".format(currentInstruction))
	print("="*consoleWidth, end="", flush=True)

"""
	Interpretes responses from the printer. Updates temperatures/status when necessary.
	false = busy/not ready
	true = ready for next instruction
"""
def ParseResponse(resp):
	resp = resp.strip()

	# Check for temps first
	if resp.startswith("T:") or resp.startswith("ok T:"):
		regResults = findall(TempRegex, resp)
		if len(regResults) >= 4:
			global extruderTemp
			global extruderTempMax
			global bedTemp
			global bedTempMax
			extruderTemp = regResults[0]
			extruderTempMax = regResults[1]
			bedTemp = regResults[2]
			bedTempMax = regResults[3]

	if resp.startswith("ok T:"): # Ensure we do not interpret the temperature's OK message as a move/temp OK message
		return False

	if resp.startswith("ok"):
		printerStatus = "Printing"
		return True

	if resp == "echo:busy: processing":
		printerStatus = "Heating"

	return False

# Main
# TODO: Implement ctrl+c handling

printerPort = GetPrinterPort()
if printerPort == None:
	print("Printer port not found, is your printer connected?\nWaiting for printer...")
	while True:
		printerPort = GetPrinterPort()
		if printerPort != None:
			break
		sleep(1)

print("Found printer at port {0}".format(printerPort))

serialPort = Serial(printerPort, baudrate=115200)

gcode = []
while True:
	path = input("Input GCode file path: ")
	if path == "" or not os.path.isfile(path):
		print("File not found")
		continue
	print("Reading file to memory")
	with open(path, "r") as dat:
		for inst in dat.read().split("\n"):
			inst = inst.strip()
			if inst == "": # Empty line, ignore
				continue
			if inst.startswith(";"): # Line is a comment, ignore
				continue
			if inst.startswith("M105"): # Temperature request, ignore
				continue
			gcode.append(StripGCode(inst))
	break

# Attempt to communicate with printer
if not serialPort.isOpen():
	serialPort.Open()
	print("Connected, sending test message")

serialPort.write(b"M115\n")
resp = serialPort.readline().decode()
print("Printer responds: {0}".format(resp))

intervalClock = time()
screenClock = time()
totalTime = time()

for instruction in gcode:
	if time() - intervalClock > TempInterval:
		intervalClock = time()
		serialPort.write(b"M105\n")
	currentInstruction = instruction
	serialPort.write("{0}\n".format(instruction).encode())
	WriteScreen()

	# Parse response from printer
	while True:
		if time() - screenClock >= RefreshInterval:
			screenClock = time()
			WriteScreen()
		if ParseResponse(serialPort.readline().decode()):
			break
		sleep(0.001)

ClearScreen()
print("Print finished in {0}".format(FormatSeconds(time() - totalTime)))