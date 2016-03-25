#!/bin/bash

trap ctrl_c INT
function ctrl_c() {
  pkill nc
  exit
}

bash list_repos_and_branches.sh > x
cat x | nc teamcity.rhi 3333

while :
do
  echo -e "HTTP/1.1 200 OK\n\n" | nc -w 30 -l -p 3333 > /dev/null
  echo "checking"
  cp x y
  bash list_repos_and_branches.sh > x
  if ! cmp x y >/dev/null 2>&1
  then
    echo updating
    cat x | nc teamcity.rhi 3333
  fi
done
