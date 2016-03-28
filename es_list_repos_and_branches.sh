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
    fi
    echo "+BRANCH " $branch $version
    echo $config
    echo "-BRANCH "
  done
  echo "-REPO"
  popd > /dev/nul
}

for repo in $(find . -type d -name '*.git' -not -name '*.wiki.git')
do
  lsb $repo
done

popd > /dev/null
