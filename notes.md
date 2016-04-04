GitLab setup
---

* Install according to instructions on gitlab site (I recommend using centos7 minimal)
* edit /opt/gitlab/embedded/service/gitlab-rails/config/gitlab.yml to set your production hostname (you'll need to run `gitlab-ctl reconfigure` afterwards)
* install netcat (`yum install nc`)
* install git (`yum install git`)
* install tmux (`yum install tmux`)
* create a teamcity user
* create a private/public key pair for the teamcity user
* create a test project
* add teamcity user to test project
* Clone git@github.com:EnokiSolutions/es-workflow.git
* start `tmux`
* `cd /var/opt/gitlab/git-data/repositories`
* `ln -s ~/es-workflow/es_list_repos_and_branches.sh es_list_repos_and_branches.sh`
* `chmod 755 es_list_repos_and_branches.sh`
* `ln -s ~/es-workflow/es_watcher.sh es_watcher.sh`
* `chmod 755 es_watcher.sh`
* edit `es_watcher.sh` and set the teamcity hostname to your teamcity server hostname
* edit `/opt/gitlab/embedded/service/gitlab-shell/hooks/post-receive` (this has to be done after every update)
* add above the exit 0 line```
  `echo -e"\n" | nc localhost 3333`
  exit 0
```

Teamcity 9.1.x Setup
---

* Create a directory for executables and add it to the system path (e.g. C:\bin)
* Install Visual Studio for c# (or just the build components)
* Install git if needed (Comes with Visual Studio 2015 now)
* Clone git@github.com:EnokiSolutions/es-workflow.git
* Install nuget (e.g. copy nuget.exe into C:\bin)
* Install es.*.exe into C:\bin
* Install teamcity, run as system
* set system environment variables if different than the defaults:
** TCG_DIR=C:\TeamCityData\config\projects
** TCG_PK=C:\TeamCityData\config\id_rsa (for teamcity user in gitlab)
* run `es_catcher.sh`
* edit C:\Windows\System32\config\systemprofile\AppData\Roaming\NuGet\NuGet.Config
** add `<add key="nuget.es" value="http://nuget.es/api/nuget" />` to `<packageSources>` and `<activePackageSource>`


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

* Install virtualbox and setup a centos7 vm using bridged networking
* Install gitlab
* run `gitlab-ctl reconfigure`
* edit /opt/gitlab/embedded/service/gitlab-rails/config/gitlab.yml and change the production host to `gitlab.es`
* run `gitlab-ctl reconfigure` again
* Install three virtual loopback devices, disable ipv6, and assign one to an ipv4 static address of 172.16.0.1, one to 172.16.0.2, and one to 172.16.0.3 (subnet mask is 255.240.0.0, use 8.8.8.8 and 8.8.4.4 for DNS)
* Edit C:\Windows\system32\drivers\etc\hosts
* alias
```
172.16.0.1	nuget.es
172.16.0.2	teamcity.es
172.16.0.3	youtrack.es
<ipaddr of virtual machine running gitlab>	gitlab.es
```
* Since you'll be running IIS (for the nuget server) you need to restrict IIS to using only one IP address. Run ```netsh http add iplisten 172.16.0.1``` as administrator to do this.
* When installing teamcity use port 80 and the default paths, but edit the serverUrl to http://teamcity.es:80 use the system account, and don't start the services.
* Edit the C:\TeamCity\conf\server.xml file and add the attribute `address="172.16.0.2"` to the `<Connector` node
* Now use the Local Services panel to start teamcity and the build agent.
* goto http://teamcity.es/
* Change the data directory to C:\TeamCityData\ and use the internal hsqldb
* When installing youtrack use a base url of http://youtrack.es and port 80 and in the advanced settings change the listen address to 172.16.0.3
