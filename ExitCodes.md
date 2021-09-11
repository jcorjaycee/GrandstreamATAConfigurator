# Exit Codes Guide

Positive exit codes indicate that the program was successful, but exited due to user error.

Negative exit codes indicate a fatal error in the program itself.

| Exit Code | Meaning                                                                                                         | Error Location
| --------- | --------------------------------------------------------------------------------------------------------------- | --------------
| 2         | The program did its job, however it failed to authenticate after three prompts from the user.                   | Program.cs:212
| -1        | The program was unable to locate any valid network interfaces.                                                  | NetworkUtils.cs:99
| -2        | The program was unable to query the ATA for a version number. Usually occurs due to SSH requests being denied.  | Program.cs:575
| -3        | During configuration, usually after a factory reset, the program was unable to reauthenticate to the ATA.       | Program.cs:388
| -4        | During configuration, the ATA was unable to connect to the ATA for an unspecified reason.                       | Program.cs:395
| -5        | When attempting to set up the HTTP upgrade server, another program was occupying the desired port.              | Server.cs:31