#!/bin/bash

trap ctrl_c INT
function ctrl_c() {
  pkill nc
  exit
}

bash es_list_repos_and_branches.sh > x
cat x | nc ${TEAMCITY_HOST} 3333

while :
do
  echo -e "HTTP/1.1 200 OK\n\n" | nc -i 3 -l -p 3333 > /dev/null
  echo "checking"
  cp x y
  bash es_list_repos_and_branches.sh > x
  if ! cmp x y >/dev/null 2>&1
  then
    echo updating
    cat x | nc ${TEAMCITY_HOST} 3333
  fi
done
