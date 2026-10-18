#
# DFAgent
#

##
## To add this as a submodule to the project, run this command in the 
## root folder
##

git submodule add https://github.com/DarkFactorAS/AIAgents.git .agents

##
## Make sure that the subfolder is not present already.
## If it is, you can force the subfolder retreval
##

git submodule update --init --recursive