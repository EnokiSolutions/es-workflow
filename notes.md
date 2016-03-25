GitLab setup
---

* Install according to instructions on gitlab site (I recommend using centos7 minimal)


Teamcity Setup
---

* Create a directory for executables and add it to the system path (e.g. C:/bin)
* Install Visual Studio for c# (or just the build components)
* Install git if needed (Comes with Visual Studio 2015 now)
* Install nuget
* Clone git@github.com:EnokiSolutions/es-workflow.git


Youtrack Setup
---

Nuget Server Setup
---
* Install Visual Studio 2015
* Enable IIS in the windows features, include the ASP.NET 4.6 support

Developer Setup
---

Sandbox Setup on Windows
---

If you want to try out everything on a single box using fake hostnames

* Install three virtual loopback devices
* Assign one an ipv4 static address of 172.16.0.1, one to 172.16.0.2, and one to 172.16.0.3 (subnet mask is 255.240.0.0, use 8.8.8.8 and 8.8.4.4 for DNS)
* Edit C:\Windows\system32\driver\etc\hosts
* alias ```nuget.es 172.16.0.1
teamcity.es 172.16.0.2
youtrack.es 172.16.0.3```
* Since you'll be running IIS (for the nuget server) you need to restrict IIS to using only one IP address. Run ```netsh http add ipaddress 172.16.0.1``` as administrator to do this.
* Install virtualbox and setup a centos7 vm using bridged networking
