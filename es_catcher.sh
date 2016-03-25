#!/bin/bash
while :
do
 echo "OK" | nc -l -p 3333 > x
 cat x
 ch.tcg x
 echo
done
