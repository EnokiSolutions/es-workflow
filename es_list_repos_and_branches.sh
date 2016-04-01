#!/bin/bash
pushd /var/opt/gitlab/git-data/repositories > /dev/null
gitlaburl="git@${GITLAB_HOST}:"

function lsb {
  echo "+REPO " ${1/\.\//${gitlaburl}}
  pushd $1 > /dev/nul
  for branch in $(ls refs/heads)
  do
    version=$(git show $branch:version.txt 2> /dev/null)
    if [ $? != 0 ]
    then
      version="0.0.0"
    fi
    config=$(git show $branch:build.tcg 2> /dev/null )
    if [ $? != 0 ]
    then
      config="skip"
    else
      name=$(git show master:sln.vsg | grep name)
      name=${name/%,/}
      dotSettings=$(git cat-file -p $branch^{tree} | grep sln.DotSettings)
      if [[ -n $dotSettings ]]
      then
        dotSettings="true"
      else
        dotSettings="false"
      fi
      testAssembly=$(git cat-file -p $branch^{tree} | grep tree | grep Test | while read l; do echo "${l:53}"; done)
      if [[ -n $testAssembly ]]
      then
        testAssembly="true"
      else
        testAssembly="false"
      fi
      assemblies=$(git cat-file -p $branch^{tree} | grep tree | grep -v Test | while read l; do echo "${l:53}"; done)
      config=${config/$$ /}
      config=${config/$'{'/}
      addconfig="{$name,\"assemblies\":[";
      for assembly in $assemblies
      do
        addconfig="$addconfig\"$assembly\","
      done
      addconfig=${addconfig/%,/}
      config="$addconfig],\"dotSettings\":$dotSettings,\"test\":$testAssembly,$config"
    fi
    echo "+BRANCH " $branch $version
    echo "$config"
    echo "-BRANCH "
  done
  echo "-REPO"
  popd > /dev/nul
}

for repo in $(find . -type d -name '*.git' -not -name '*.wiki.git' -not -name '*\+deleted*')
do
  lsb $repo
done

popd > /dev/null
