#!/bin/bash
while :
do
 echo "OK" | nc -l -p 3333 > x
 cat x
 es.tcg x
 echo
done
