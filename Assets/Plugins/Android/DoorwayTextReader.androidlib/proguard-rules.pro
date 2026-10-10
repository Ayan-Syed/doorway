# SignReader (C#) finds these by name through JNI, so a minified build must not rename or drop them.
-keep class com.ayansyed.doorway.DoorwayTextReader { *; }
-keep interface com.ayansyed.doorway.DoorwayTextReader$Listener { *; }
-keep class com.ayansyed.doorway.DoorwayStepSensor { *; }
